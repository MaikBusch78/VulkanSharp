using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace VulkanSharp.Generator
{
    public class GeneratorBase
    {
        public static bool WriteAliases = false;
        public static bool WriteComments = false;

        protected string TranslateCName(string name)
        {
            StringWriter sw = new StringWriter();
            bool first = true;

            foreach (var part in name.Split('_'))
            {
                if (first)
                {
                    first = false;
                    if (name.StartsWith("VK", StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                if (specialParts.ContainsKey(part))
                    sw.Write(specialParts[part]);
                else
                if (part.Length > 0)
                {
                    var chars = part.ToCharArray();
                    if (chars.All(c => char.IsUpper(c)))
                        sw.Write(part[0] + part.Substring(1).ToLower());
                    else
                    {
                        string formatted = "";
                        bool upIt = true;
                        bool wasLower = false;
                        bool wasDigit = false;
                        foreach (var ch in chars)
                        {
                            formatted += upIt ? char.ToUpper(ch) : (wasLower ? ch : char.ToLower(ch));
                            upIt = char.IsDigit(ch);
                            wasLower = char.IsLower(ch);
                            if (wasDigit && char.ToLower(ch) == 'd')
                                upIt = true;
                            wasDigit = char.IsDigit(ch);
                        }
                        sw.Write(formatted);
                    }
                }
            }

            return sw.ToString();
        }

        static Dictionary<string, string> specialParts = new Dictionary<string, string>
        {
            { "AMD", "Amd" },
            { "API", "Api" },
            { "EXT", "Ext" },
            { "ID", "ID" },
            { "IOS", "IOS" },
            { "KHR", "Khr" },
            { "KHX", "Khx" },
            { "LOD", "LOD" },
            { "1D", "1D" },
            { "2D", "2D" },
            { "3D", "3D" },
            { "MACOS", "MacOS" },
            { "NV", "Nv" },
            { "NVX", "Nvx" },
            { "NN", "Nn" },

            { "AABB", "AABB" },
            { "ASTC", "ASTC" },
        };

        protected string TranslateCNameEnumField(string name, string csEnumName)
        {
            string fName = TranslateCName(name);
            string prefix = csEnumName, suffix = null;
            bool isExtensionField = false;
            string extension = null;

            foreach (var ext in vendorTags)
            {
                if (prefix.EndsWith(ext.Value.csName))
                {
                    prefix = prefix.Substring(0, prefix.Length - ext.Value.csName.Length);
                    suffix = ext.Value.csName;
                }
                else if (fName.EndsWith(ext.Value.csName))
                {
                    isExtensionField = true;
                    extension = ext.Value.csName;
                }
            }

            if (prefix.EndsWith("Flags"))
            {
                prefix = prefix.Substring(0, prefix.Length - 5);
                suffix = "Bit" + suffix;
            }

            if (fName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                fName = fName.Substring(prefix.Length);

            if (!char.IsLetter(fName[0]))
            {
                switch (csEnumName)
                {
                    case "ImageType":
                        fName = "Image" + fName;
                        break;
                    case "ImageViewType":
                        fName = "View" + fName;
                        break;
                    case "QueryResultFlags":
                        fName = "Result" + fName;
                        break;
                    case "SampleCountFlags":
                        fName = "Count" + fName;
                        break;
                    case "ImageCreateFlags":
                        fName = "Create" + fName;
                        break;
                    case "ShadingRatePaletteEntryNv":
                        fName = "X" + fName;
                        break;
                    default:
                        throw new NotImplementedException();
                }
            }
            if (suffix != null)
            {
                if (fName.EndsWith(suffix))
                    fName = fName.Substring(0, fName.Length - suffix.Length);
                else if (isExtensionField && fName.EndsWith(suffix + extension))
                    fName = fName.Substring(0, fName.Length - suffix.Length - extension.Length) + extension;
            }

            return fName;
        }

        protected Dictionary<string, PlatformInfo> platforms = new Dictionary<string, PlatformInfo>();

        protected Dictionary<string, VendorTagInfo> vendorTags = new Dictionary<string, VendorTagInfo>();

        public class PlatformInfo
        {
            public string name;
            public string protect;
            public string comment;
        }

        public class VendorTagInfo
        {
            public string name;
            public string csName;
            public string author;
            public string contact;
        }

        protected void LearnPlatforms(XElement specTree)
        {
            var platformsX = specTree.Elements("platforms").Elements("platform").ToList();

            foreach (var platformX in platformsX)
            {
                var name = platformX.Attribute("name").Value;

                platforms[name] = new PlatformInfo()
                {
                    name = name,
                    protect = platformX.Attribute("protect").Value,
                    comment = platformX.Attribute("comment").Value,
                };
            }
        }

        protected void LearnVendorTags(XElement specTree)
        {
            var tagsX = specTree.Elements("tags").Elements("tag").ToList();

            foreach (var tagX in tagsX)
            {
                var name = tagX.Attribute("name").Value;

                vendorTags[name] = new VendorTagInfo()
                {
                    name = name,
                    csName = name[0] + name.Substring(1).ToLower(),
                    author = tagX.Attribute("author").Value,
                    contact = tagX.Attribute("contact").Value,
                };
            }
        }

        protected string GetTypeCsName(string name, string typeName = "type")
        {
            if (typesTranslation.ContainsKey(name))
                return typesTranslation[name];

            string csName;

            if (name.StartsWith("Vk", StringComparison.OrdinalIgnoreCase))
            {
                csName = name.Substring(2);
            }
            else if (name.EndsWith("_t"))
            {
                if (!basicTypesMap.ContainsKey(name))
                    throw new NotImplementedException(string.Format("Mapping for the basic type {0} isn't supported", name));

                csName = basicTypesMap[name];
            }
            else
            {
                if (typeName == "type" && !knownTypes.Contains(name) && !name.StartsWith("PFN_", StringComparison.Ordinal))
                    Console.WriteLine("warning: {0} name '{1}' doesn't start with Vk prefix or end with _t suffix", typeName, name);
                csName = name;
            }

            foreach (var ext in vendorTags)
            {
                if (csName.EndsWith(ext.Key))
                {
                    csName = csName.Substring(0, csName.Length - ext.Key.Length) + ext.Value.csName;
                }
            }

            return csName;
        }

        protected Dictionary<string, string> typesTranslation = new Dictionary<string, string>()
        {
            { "ANativeWindow", "IntPtr" },
            { "AHardwareBuffer", "IntPtr" }, // TODO: richtig ?
            { "HWND", "IntPtr" },
            { "HINSTANCE", "IntPtr" },
            { "HANDLE", "IntPtr" },
            { "DWORD", "UInt32" },
            { "SECURITY_ATTRIBUTES", "SecurityAttributes" },
            { "Display", "IntPtr" },  // this is now broken, as Handles use DisplayKHR instead of it
			{ "RROutput", "UInt32" },
        };

        static Dictionary<string, string> basicTypesMap = new Dictionary<string, string>
        {
            { "int32_t", "Int32" },
            { "uint32_t", "UInt32" },
            { "uint64_t", "UInt64" },
            { "uint8_t", "byte" },
            { "size_t", "UIntPtr" },
            { "xcb_connection_t", "IntPtr" },
            { "xcb_window_t", "IntPtr" },
            { "xcb_visualid_t", "Int32" },
            { "zx_handle_t", "IntPtr" },
        };

        HashSet<string> knownTypes = new HashSet<string>
        {
            "void",
            "char",
            "float",
        };

        protected string GetEnumCsName(string name, bool bitmask)
        {
            string csName = GetTypeCsName(name, "enum");

            if (bitmask)
                csName = GetFlagBitsName(csName);
            
            return csName;
        }

        protected string GetFlagBitsName(string name)
        {
            string result = name;

            string extStr = null;
            foreach (var ext in vendorTags)
            {
                if (name.EndsWith(ext.Value.csName))
                {
                    extStr = ext.Value.csName;
                    result = name.Substring(0, name.Length - ext.Value.csName.Length);
                    break;
                }
            }

            if (result.EndsWith("FlagBits"))
                result = result.Substring(0, result.Length - "Bits".Length) + "s";

            if (extStr != null)
                result += extStr;

            return result;
        }
    }
}

