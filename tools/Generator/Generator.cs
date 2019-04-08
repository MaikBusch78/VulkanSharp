using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VulkanSharp.Generator
{
    [Flags]
    public enum UsingNamespaceFlags
    {
        Interop = 1,
        Collections = 2,
        Vulkan = 4,
        VulkanInterop = 8
    }

    public class Generator : GeneratorBase
    {
        string specXmlPath;
        XElement xSpecTree;

        string outputDir;

        Writer writer;

        bool isUnion;
        bool isInterop;
        bool needsMarshalling;

        Dictionary<string, IncludeInfo> includeInfos = new Dictionary<string, IncludeInfo>();
        Dictionary<string, DefineInfo> defineInfos = new Dictionary<string, DefineInfo>();
        Dictionary<string, WsiTypeInfo> wsiTypes = new Dictionary<string, WsiTypeInfo>();

        Dictionary<string, BaseTypeInfo> basetypeInfos = new Dictionary<string, BaseTypeInfo>();

        Dictionary<string, EnumInfo> bitmaskInfos = new Dictionary<string, EnumInfo>();
        Dictionary<string, HandleInfo> handles = new Dictionary<string, HandleInfo>();

        Dictionary<string, EnumInfo> enumInfos = new Dictionary<string, EnumInfo>();
        Dictionary<string, StructInfo> structures = new Dictionary<string, StructInfo>()
        {
            { "SecurityAttributes", new StructInfo { name = "SECURITY_ATTRIBUTES", csName = "SecurityAttributes", needsMarshalling = false } }
        };

        Dictionary<string, List<XElement>> enumExtensionInfos = new Dictionary<string, List<XElement>>();

        class TypeInfo
        {
            public TypeInfo alias;

            public string name;
            public string csName;
        }

        class BaseTypeInfo : TypeInfo
        {
            public BaseTypeInfo typedef;
        }

        class IncludeInfo : TypeInfo
        {
            public string value;
        }

        class WsiTypeInfo : TypeInfo
        {
            public IncludeInfo requires;
        }

        class DefineInfo : TypeInfo
        {
            public string[] value;
        }

        class MemberTypeInfo : TypeInfo
        {
            public Dictionary<string, string> members; // Mapping from CsName to CName.
        }

        class EnumInfo : MemberTypeInfo
        {
        }

        StructInfo currentStructInfo;

        class StructInfo : MemberTypeInfo
        {
            public XElement element;
            public bool needsMarshalling;
        }

        class StructMemberInfo
        {
            public string csType;
            public string csName;
            public bool isPointer;
            public bool isHandle;
        }

        class HandleInfo
        {
            public string name;
            public string type;
            public List<XElement> commands = new List<XElement>();
        }

        class ParamInfo
        {
            public string csName;
            public string csType;
            public string type;
            public string len;
            public bool isOut;
            public bool isStruct;
            public bool isHandle;
            public bool isFixed;
            public bool isPointer;
            public bool isConst;
            public bool needsMarshalling;
            public ParamInfo lenArray;
            public bool isArray;
            public bool isNullable;
            public string constValue;

            public string MarshalSizeSource(Generator generator, bool isInInterop)
            {
                if (generator.enumInfos.ContainsKey(csType))
                    return "4"; // int enum

                return string.Format("Marshal.SizeOf (typeof ({0}{1}))",
                                      isInInterop ? "Interop." : "",
                                      isHandle ? generator.GetHandleType(generator.handles[csType]) : csType);
            }
        }

        public Generator(string specXmlPath, string outputDir)
        {
            this.specXmlPath = specXmlPath;
            this.outputDir = outputDir;
        }

        public void Run()
        {
            LoadSpecification();

            LearnPlatforms(xSpecTree);
            LearnVendorTags(xSpecTree);
            LearnTypesHeader();

            var origOutputDir = outputDir;
            outputDir = Path.Combine(origOutputDir, "Vulkan");
            GenerateFileEnumsCs();

            LearnHandles();
            LearnStructsAndUnions();
            GenerateFileStructsCs();
            GenerateFileUnionsCs();
            GenerateFileImportedCommandsCs();
            GenerateFileCommandsCs();
            GenerateFileHandlesCs();

            outputDir = Path.Combine(origOutputDir, "Platforms");
            GeneratePlatformExtensions();

            outputDir = Path.Combine(origOutputDir, "Vulkan");
            WriteFileTypesXml();
        }

        void LoadSpecification()
        {
            xSpecTree = XElement.Load(specXmlPath);

            if (xSpecTree.Name != "registry")
                throw new Exception("problem parsing the file, top element is not 'registry'");

            Console.WriteLine("Specification file {0} loaded", specXmlPath);
        }

        void LearnTypesHeader()
        {
            var xTypes = xSpecTree.Elements("types").Elements("type").ToList();

            int t = 0;

            // include
            for (; t < xTypes.Count; t++)
            {
                var typeX = xTypes[t];

                var category = typeX.Attribute("category")?.Value;

                if (category == null)
                {
                    break;
                }

                if (category == "include")
                {
                    var name = typeX.Attribute("name").Value;

                    includeInfos[name] = new IncludeInfo()
                    {
                        name = name,
                        value = typeX.Value,
                    };
                    continue;
                }

                break;
            }

            // requires
            for (; t < xTypes.Count; t++)
            {
                var typeX = xTypes[t];

                var requires = typeX.Attribute("requires")?.Value;

                if (requires != null)
                {
                    var name = typeX.Attribute("name").Value;

                    wsiTypes[name] = new WsiTypeInfo()
                    {
                        name = name,
                        requires = includeInfos[requires],
                    };
                    continue;
                }

                break;
            }

            // define
            for (; t < xTypes.Count; t++)
            {
                var typeX = xTypes[t];

                var category = typeX.Attribute("category")?.Value;

                if (category == null)
                {
                    break;
                }

                if (category == "define")
                {
                    var name = typeX.Attribute("name")?.Value ?? typeX.Element("name").Value;

                    defineInfos[name] = new DefineInfo()
                    {
                        name = name,
                        value = typeX.Value.Split('\n'),
                    };
                    continue;
                }

                break;
            }

            // basetype
            for (; t < xTypes.Count; t++)
            {
                var typeX = xTypes[t];

                var category = typeX.Attribute("category")?.Value;

                if (category != null && category == "bitmask")
                {
                    break;
                }

                if (category != null && category == "basetype")
                {
                    var name = typeX.Element("name").Value;
                    var type = typeX.Element("type").Value;

                    basetypeInfos[name] = new BaseTypeInfo()
                    {
                        name = name,
                        csName = GetTypeCsName(name),
                        typedef = new BaseTypeInfo()
                        {
                            name = type,
                            csName = GetTypeCsName(type),
                        },
                    };
                    continue;
                }

                // Ignore "Basic C types"
            }
        }

        string InteropNamespace
        {
            get
            {
                return string.Format("{0}Interop", platform == null ? "" : (platform + "."));
            }
        }

        string Namespace
        {
            get
            {
                return string.Format("Vulkan{0}", platform == null ? "" : ("." + platform));
            }
        }

        List<Tuple<XElement, List<XElement>>> RequireTypes(string requireKind, string category)
        {
            var result = new List<Tuple<XElement, List<XElement>>>();

            var requiredFeatureTypes = xSpecTree.Elements("feature").Elements("require").Elements(requireKind).ToList();
            var requiredSupportedExtensionTypes = SupportedExtensions().Elements("require").Elements(requireKind).ToList();
            var requiredDisabledExtensionTypes = DisabledExtensions().Elements("require").Elements(requireKind).ToList();

            var typesWithCategory = xSpecTree.Elements("types").Elements(requireKind).Where(e => e.Attribute("category") != null).ToList();

            var xCategoryTypes1 = typesWithCategory.Where(t => t.Attribute("category").Value == category).Where(t => t.Attribute("alias") == null).ToList();
            var xCategoryTypes2 = typesWithCategory.Where(t => t.Attribute("category").Value == category).Where(t => t.Attribute("alias") != null).ToList();

            foreach (var xCategoryType in xCategoryTypes1)
            {
                if (requiredDisabledExtensionTypes.Any(r => r.Attribute("name").Value == xCategoryType.Attribute("name").Value))
                {
                    continue;
                }
                if (requiredFeatureTypes.Any(r => r.Attribute("name").Value == xCategoryType.Attribute("name").Value))
                {
                    result.Add(Tuple.Create(xCategoryType, xCategoryTypes2.Where(a => a.Attribute("alias").Value == xCategoryType.Attribute("name").Value).ToList()));

                    continue;
                }
                if (requiredSupportedExtensionTypes.Any(r => r.Attribute("name").Value == xCategoryType.Attribute("name").Value))
                {
                    result.Add(Tuple.Create(xCategoryType, xCategoryTypes2.Where(a => a.Attribute("alias").Value == xCategoryType.Attribute("name").Value).ToList()));

                    continue;
                }

                result.Add(Tuple.Create(xCategoryType, xCategoryTypes2.Where(a => a.Attribute("alias").Value == xCategoryType.Attribute("name").Value).ToList()));
            }

            return result;
        }

        #region GenerateFileEnumsCs

        void GenerateFileEnumsCs()
        {
            writer = new Writer(Path.Combine(outputDir, "Enums.cs"));

            bool prependNewLine = false;

            var typesWithCategory = xSpecTree.Elements("types").Elements("type").Where(e => e.Attribute("category") != null).ToList();

            var xBitmaskTypes1 = typesWithCategory.Where(t => t.Attribute("category").Value == "bitmask").Where(t => t.Attribute("alias") == null).ToList();
            var xBitmaskTypes2 = typesWithCategory.Where(t => t.Attribute("category").Value == "bitmask").Where(t => t.Attribute("alias") != null).ToList();
            foreach (var xBitmaskType in xBitmaskTypes1)
            {
                if (prependNewLine) writer.WriteLine();

                var xAliases = xBitmaskTypes2.Where(a => a.Attribute("alias").Value == xBitmaskType.Element("name").Value).ToList();

                prependNewLine = GeneratorFlags(xBitmaskType, xAliases);
            }

            var xEnumTypes1 = typesWithCategory.Where(t => t.Attribute("category").Value == "enum").Where(t => t.Attribute("alias") == null).ToList();
            var xEnumTypes2 = typesWithCategory.Where(t => t.Attribute("category").Value == "enum").Where(t => t.Attribute("alias") != null).ToList();
            foreach (var xEnumType in xEnumTypes1)
            {
                if (prependNewLine) writer.WriteLine();

                var xAliases = xEnumTypes2.Where(a => a.Attribute("alias").Value == xEnumType.Attribute("name").Value).ToList();

                prependNewLine = GeneratorEnum(xEnumType, xAliases);
            }

            writer.FinalizeFile();
            writer = null;
        }

        bool GeneratorFlags(XElement xBitmaskType, List<XElement> xAliases)
        {
            var name = xBitmaskType.Element("name").Value;
            var requires = xBitmaskType.Attribute("requires")?.Value;

            var xEnums = requires != null ? xSpecTree.Elements("enums").Where(e => e.Attribute("name").Value == requires).Single() : null;
            var bitmask = true;

            var currentEnumInfo = new EnumInfo
            {
                name = name,
                csName = GetEnumCsName(name, bitmask),
                members = new Dictionary<string, string>()
            };

            WriteEnum(currentEnumInfo, xAliases, bitmask, xEnums);

            return true;
        }

        bool GeneratorEnum(XElement xEnumType, List<XElement> xAliases)
        {
            var name = xEnumType.Attribute("name").Value;

            var xEnums = xSpecTree.Elements("enums").Where(e => e.Attribute("name").Value == name).SingleOrDefault();
            var bitmask = xEnums != null && xEnums.Attribute("type").Value == "bitmask";

            var currentEnumInfo = new EnumInfo
            {
                name = name,
                csName = GetEnumCsName(name, bitmask),
                alias = null,
                members = new Dictionary<string, string>()
            };

            if (enumInfos.ContainsKey(currentEnumInfo.csName) == false)
            {
                WriteEnum(currentEnumInfo, xAliases, bitmask, xEnums);

                return true;
            }
            else
            {
                return false;
            }
        }

        void WriteEnum(EnumInfo currentEnumInfo, List<XElement> xAliases, bool bitmask, XElement xEnums)
        {
            typesTranslation[currentEnumInfo.name] = currentEnumInfo.csName;

            WriteEnum(currentEnumInfo, bitmask, xEnums);

            enumInfos[currentEnumInfo.csName] = currentEnumInfo;

            foreach (var alias in xAliases)
            {
                var aliasName = alias.Attribute("name").Value;

                var aliasCurrentEnumInfo = new EnumInfo
                {
                    name = aliasName,
                    csName = GetEnumCsName(aliasName, bitmask),
                    alias = currentEnumInfo,
                };

                if (WriteAliases) throw new NotImplementedException();

                typesTranslation[aliasCurrentEnumInfo.name] = aliasCurrentEnumInfo.alias.csName;

                enumInfos[aliasCurrentEnumInfo.csName] = (EnumInfo)aliasCurrentEnumInfo.alias;
            }
        }

        void WriteEnum(EnumInfo currentEnumInfo, bool bitmask, XElement xEnums)
        {
            if (bitmask) writer.IndentWriteLine("[Flags]");

            writer.IndentWriteLine("public enum {0} : {1}", currentEnumInfo.csName, bitmask ? "uint" : "int");
            writer.IndentWriteLineBraceOpen();
            {
                if (bitmask)
                {
                    writer.IndentWriteLine("{0} = {1},", "None", 0);
                }

                if (xEnums != null)
                {
                    foreach (var e in xEnums.Elements("enum"))
                    {
                        WriteEnumField(currentEnumInfo, null, e);
                    }
                }

                var features = xSpecTree.Elements("feature").ToList();

                foreach (var extension in features)
                {
                    var enumExtensions = extension.Elements("require").Elements("enum").Where(e => e.Attribute("extends") != null).ToList();

                    foreach (var e in enumExtensions.Where(e => e.Attribute("extends").Value == currentEnumInfo.name))
                    {
                        WriteEnumField(currentEnumInfo, extension, e);
                    }
                }

                List<XElement> supportedExtensions = SupportedExtensions();

                foreach (var extension in supportedExtensions)
                {
                    var enumExtensions = extension.Elements("require").Elements("enum").Where(e => e.Attribute("extends") != null).ToList();

                    foreach (var e in enumExtensions.Where(e => e.Attribute("extends").Value == currentEnumInfo.name))
                    {
                        WriteEnumField(currentEnumInfo, extension, e);
                    }
                }
            }
            writer.IndentWriteLineBraceClose();
        }

        void WriteEnumField(EnumInfo currentEnumInfo, XElement xFeatureOrExtension, XElement xEnum)
        {
            string csName = TranslateCNameEnumField(xEnum.Attribute("name").Value, currentEnumInfo.csName);

            if (csName != "None" && currentEnumInfo.members.ContainsKey(csName) == false)
            {
                var value = xEnum.Attribute("value")?.Value;

                if (value == null && xEnum.Attribute("bitpos") != null)
                {
                    value = string.Format("0x{0:X}", 1 << ((int)xEnum.Attribute("bitpos")));
                }

                if (value == null && xEnum.Attribute("offset") != null)
                {
                    int dir = xEnum.Attribute("dir")?.Value == "-" ? -1 : +1;
                    int number = xEnum.Attribute("extnumber") != null ? (int)xEnum.Attribute("extnumber") : (int)xFeatureOrExtension.Attribute("number");

                    value = (dir * (1000000000 + (number - 1) * 1000 + (int)xEnum.Attribute("offset"))).ToString();
                }

                if (WriteAliases) throw new NotImplementedException();

                if (value == null && xEnum.Attribute("alias") != null)
                {
                    var valueCsName = TranslateCNameEnumField(xEnum.Attribute("alias").Value, currentEnumInfo.csName);

                    currentEnumInfo.members[csName] = currentEnumInfo.members[valueCsName];
                }
                else
                {
                    writer.IndentWriteLine("{0} = {1},", csName, value);

                    currentEnumInfo.members[csName] = xEnum.Attribute("name").Value;
                }
            }
        }

        #endregion

        #region GenerateFileStructsCs

        void LearnStructsAndUnions()
        {
            bool prependNewLine = false;

            foreach (var xType in RequireTypes("type", "struct"))
            {
                if (prependNewLine) writer.WriteLine();

                prependNewLine = LearnStructure(xType.Item1, xType.Item2);
            }

            foreach (var xType in RequireTypes("type", "union"))
            {
                if (prependNewLine) writer.WriteLine();

                prependNewLine = LearnStructure(xType.Item1, xType.Item2);
            }

            CompleteMarshallingInfo();
        }

        bool LearnStructure(XElement xStruct, List<XElement> xAliases)
        {
            string name = xStruct.Attribute("name").Value;
            string csName = GetTypeCsName(name, "struct");

            if (platformExtensionsRequiredTypes != null && !platformExtensionsRequiredTypes.Contains(name) || disabledStructs.Contains(csName))
            {
                return false;
            }

            typesTranslation[name] = csName;

            structures[csName] = new StructInfo() { name = name, csName = csName, needsMarshalling = LearnStructureMembers(xStruct), element = xStruct };

            foreach (var alias in xAliases)
            {
                var aliasName = alias.Attribute("name").Value;

                var aliasCurrentStructInfo = new StructInfo
                {
                    name = aliasName,
                    csName = GetTypeCsName(aliasName),
                    alias = structures[csName],
                };

                if (WriteAliases) throw new NotImplementedException();

                typesTranslation[aliasCurrentStructInfo.name] = aliasCurrentStructInfo.alias.csName;

                structures[aliasCurrentStructInfo.csName] = (StructInfo)aliasCurrentStructInfo.alias;
            }

            return false;
        }

        HashSet<string> disabledStructs = new HashSet<string>
        {
            "XlibSurfaceCreateInfoKhr",
            //"ImagePipeSurfaceCreateInfoFuchsia",
            "XcbSurfaceCreateInfoKhr",
            "WaylandSurfaceCreateInfoKhr",
            "MirSurfaceCreateInfoKhr",
            // "AndroidSurfaceCreateInfoKhr",
            // "Win32SurfaceCreateInfoKhr",
            "ImportMemoryWin32HandleInfoNv",
            "ExportMemoryWin32HandleInfoNv",
            // "Win32KeyedMutexAcquireReleaseInfoNv",
            "ImportMemoryWin32HandleInfoKhr",
            "ExportMemoryWin32HandleInfoKhr",
            "ImportSemaphoreWin32HandleInfoKhr",
            "ExportSemaphoreWin32HandleInfoKhr",
            "ImportFenceWin32HandleInfoKhr",
            "ExportFenceWin32HandleInfoKhr",
            "PhysicalDeviceGroupProperties", // TODO: support fixed array of Handles
			"PhysicalDeviceGroupPropertiesKhx", // TODO: support fixed array of Handles
			"NativeBufferAndroid", // NativeBufferAndroid uses disabled extension
        };

        bool LearnStructureMembers(XElement xStruct)
        {
            foreach (var memberElement in xStruct.Elements("member"))
            {
                var member = memberElement.Value;
                var type = memberElement.Element("type").Value;
                var csMemberType = GetTypeCsName(type, "member");

                if (member.Contains("*")
                    || IsArray(memberElement)
                    || (structures.ContainsKey(csMemberType) && structures[csMemberType].needsMarshalling)
                    || handles.ContainsKey(csMemberType))
                {
                    return true;
                }
            }

            return false;
        }

        void CompleteMarshallingInfo()
        {
            bool changed;
            do
            {
                changed = false;
                foreach (var pair in structures)
                {
                    var info = pair.Value;
                    if (info.element == null)
                        continue;
                    var structNeedsMarshalling = LearnStructureMembers(info.element);
                    if (structNeedsMarshalling != info.needsMarshalling)
                    {
                        info.needsMarshalling = structNeedsMarshalling;
                        changed = true;
                    }
                }
            }
            while (changed);
        }

        void GenerateFileStructsCs()
        {
            writer = new Writer(Path.Combine(outputDir, "Structs.cs"), UsingNamespaceFlags.Interop, Namespace);
            isUnion = false;
            GenerateStructs();
            writer.FinalizeFile();
            writer = null;

            writer = new Writer(Path.Combine(outputDir, "Interop", "MarshalStructs.cs"), 0, "Vulkan." + InteropNamespace);
            isUnion = false;
            isInterop = true;
            GenerateStructs();
            isInterop = false;
            writer.FinalizeFile();
            writer = null;
        }

        void GenerateStructs()
        {
            bool prependNewLine = false;

            foreach (var xType in RequireTypes("type", "struct"))
            {
                WriteStructOrUnion(ref prependNewLine, xType.Item1, xType.Item2);
            }
        }

        void GenerateFileUnionsCs()
        {
            writer = new Writer(Path.Combine(outputDir, "Unions.cs"), UsingNamespaceFlags.Interop, Namespace);
            isUnion = true;
            GenerateUnions();
            writer.FinalizeFile();
            writer = null;

            writer = new Writer(Path.Combine(outputDir, "Interop", "MarshalUnions.cs"), UsingNamespaceFlags.Interop, "Vulkan." + InteropNamespace);
            isInterop = true;
            GenerateUnions();
            isInterop = false;
            isUnion = false;
            writer.FinalizeFile();
            writer = null;
        }

        void GenerateUnions()
        {
            bool prependNewLine = false;

            foreach (var xType in RequireTypes("type", "union"))
            {
                WriteStructOrUnion(ref prependNewLine, xType.Item1, xType.Item2);
            }
        }

        void WriteStructOrUnion(ref bool prependNewLine, XElement xElement, List<XElement> xAliases)
        {
            var name = xElement.Attribute("name").Value;

            if (!typesTranslation.ContainsKey(name) || (platformExtensionsRequiredTypes != null && !platformExtensionsRequiredTypes.Contains(name)))
                return;

            var csName = typesTranslation[name];
            var info = structures[csName];
            needsMarshalling = info.needsMarshalling;

            if (isInterop && !needsMarshalling)
                return;

            if (prependNewLine)
            {
                writer.WriteLine();
                prependNewLine = false;
            }

            if (isUnion && (isInterop || !needsMarshalling))
            {
                writer.IndentWriteLine("[StructLayout(LayoutKind.Explicit)]");
            }
            var mod = !isInterop ? "unsafe " : "";
            var isStruct = (isInterop || !needsMarshalling);
            var baseClass = BaseClass(xElement, isStruct) ?? "MarshalledObject";
            writer.IndentWriteLine("{0}{1} partial {2} {3}{4}", mod, isInterop ? "internal" : "public", isStruct ? "struct" : "class", csName, !isStruct ? " : " + baseClass : "");
            writer.IndentWriteLineBraceOpen();
            {
                initializeMembers = new List<StructMemberInfo>();
                arrayMembers = new List<string>();
                currentStructInfo = info;
                currentStructInfo.members = new Dictionary<string, string>();
                GenerateCodeForElements(xElement.Elements("member"), WriteMember);

                if (!isInterop)
                {
                    bool hasSType = false;
                    var values = from el in xElement.Elements("member")
                                 where (string)el.Element("name") == "sType"
                                 select el;
                    foreach (var el in values)
                    {
                        var elType = el.Element("type");
                        if (elType != null && elType.Value == "VkStructureType")
                            hasSType = true;
                    }

                    if (info.needsMarshalling)
                    {
                        string newKeyWord = baseClass != "MarshalledObject" ? "new " : "";

                        writer.WriteLine();
                        var needsInitialize = hasSType || initializeMembers.Count > 0;
                        writer.IndentWriteLine("internal " + newKeyWord + "{0}.{1}* M", InteropNamespace, csName);
                        writer.IndentWriteLineBraceOpen();
                        {
                            writer.IndentWriteLine("get {{ " + "return ({0}.{1}*)native.Handle;" + " }}", InteropNamespace, csName);
                        }
                        writer.IndentWriteLineBraceClose();

                        writer.WriteLine();
                        writer.IndentWriteLine("public {0} ()", csName);
                        writer.IndentWriteLineBraceOpen();
                        writer.IndentWriteLine("native = {0}Interop.Structure.Allocate (typeof ({1}.{2}));", InteropNamespace == "Interop" ? "" : "Vulkan.", InteropNamespace, csName);
                        if (needsInitialize)
                            WriteStructureInitialize(initializeMembers, csName, hasSType);
                        writer.IndentWriteLineBraceClose();

                        writer.WriteLine();
                        writer.IndentWriteLine("internal {0} (NativePointer pointer)", csName);
                        writer.IndentWriteLineBraceOpen();
                        writer.IndentWriteLine("native = pointer;");
                        if (needsInitialize)
                            WriteStructureInitialize(initializeMembers, csName, hasSType);
                        writer.IndentWriteLineBraceClose();

                        if (arrayMembers.Count() > 0)
                        {
                            writer.WriteLine();
                            writer.IndentWriteLine("override public void Dispose (bool disposing)");
                            writer.IndentWriteLineBraceOpen();
                            writer.IndentWriteLine("base.Dispose (disposing);");
                            writer.IndentWriteLine("if (!disposing)");
                            writer.IndentLevel++;
                            writer.IndentWriteLine("return;");
                            writer.IndentLevel--;
                            foreach (var refName in arrayMembers)
                            {
                                writer.IndentWriteLine("ref{0}.Dispose ();", refName);
                                writer.IndentWriteLine("ref{0} = null;", refName);
                            }
                            writer.IndentWriteLineBraceClose();
                        }
                    }
                }
            }
            writer.IndentWriteLineBraceClose();

            prependNewLine = true;
            return;
        }

        string BaseClass(XElement structElement, bool isStruct)
        {
            if (structElement.Attribute("structextends") != null)
            {
                var structextends = structElement.Attribute("structextends").Value.Split(',').First();

                if (isStruct && structextends != null)
                {
                    Console.WriteLine("error: structextends is not implemented for structs.");
                }
                if (!isStruct)
                {
                    return GetTypeCsName(structextends);
                }
            }

            return null;
        }

        bool WriteMember(XElement memberElement)
        {
            var parentName = memberElement.Parent.Attribute("name").Value;

            var typeElement = memberElement.Element("type");
            if (typeElement == null)
            {
                Console.WriteLine("warning: a member of the struct {0} doesn't have a 'type' node", parentName);
                return false;
            }
            var csMemberType = GetTypeCsName(typeElement.Value, "member");

            var nameElement = memberElement.Element("name");
            if (nameElement == null)
            {
                Console.WriteLine("warning: a member of the struct {0} doesn't have a 'name' node", parentName);
                return false;
            }

            string name = nameElement.Value;

            if (!isInterop && needsMarshalling && csMemberType == "StructureType" && name == "sType")
                return false;

            var isArray = false;
            var isFixedArray = false;
            bool isPointer = memberElement.Value.Contains(typeElement.Value + "*");
            bool isDoublePointer = memberElement.Value.Contains(typeElement.Value + "**") || memberElement.Value.Contains(typeElement.Value + "* const*");
            if (isPointer)
            {
                if (name.StartsWith("p")) name = name.Substring(1);
                if (name.StartsWith("p")) name = name.Substring(1);

                switch (csMemberType)
                {
                    case "void":
                        if (!isInterop && name == "Next")
                            return false;
                        csMemberType = "IntPtr";
                        break;
                    case "char":
                        csMemberType = isDoublePointer ? (isInterop ? "IntPtr" : "string[]") : "string";
                        break;
                    case "float":
                        csMemberType = isInterop ? "IntPtr" : "float";
                        isArray = true;
                        break;
                    case "SampleMask":
                    case "UInt32":
                        csMemberType = isInterop ? "IntPtr" : "UInt32";
                        isArray = true;
                        break;
                    default:
                        var lenAttribute = memberElement.Attribute("len");
                        if (lenAttribute != null || fieldCounterMap.ContainsKey(TranslateCName(name)))
                            isArray = true;
                        break;
                }
            }
            else
            if (IsArray(memberElement)
                     && GetArrayLength(memberElement) != null
                     && !(structures.ContainsKey(csMemberType)
                          && structures[csMemberType].needsMarshalling))
                isFixedArray = true;
            var csMemberName = TranslateCName(name);

            // TODO: fixed arrays of structs
            if (csMemberName.EndsWith("]"))
            {
                string array = csMemberName.Substring(csMemberName.IndexOf('['));
                csMemberName = csMemberName.Substring(0, csMemberName.Length - array.Length);
                // temporarily disable arrays csMemberType += "[]";
            }

            currentStructInfo.members[csMemberName] = nameElement.Value;

            var isCharArray = false;
            if (csMemberType == "char" && InnerValue(memberElement).EndsWith("]"))
                isCharArray = true;
            string mod = "";
            if (csMemberName.EndsWith("]"))
                mod = "unsafe fixed ";

            csMemberType = GetFlagBitsName(csMemberType);

            if (csMemberType.StartsWith("PFN_"))
                csMemberType = "IntPtr";

            if (csMemberType == "SampleMask")
                csMemberType = "UInt32";

            if (csMemberType == "Bool32" && !isInterop && needsMarshalling)
                csMemberType = "bool";

            bool needsCharCast = false;
            if (csMemberType == "char")
            {
                if (isInterop || !needsMarshalling)
                    csMemberType = "byte";
                needsCharCast = true;
            }

            string attr = "";
            string sec = isInterop ? "internal" : "public";
            if (isUnion)
                attr = "[FieldOffset (0)] ";

            if (WriteComments)
            {
                var commentElement = memberElement.Element("comment");
                if (commentElement != null)
                {
                    writer.IndentWriteLine("/// <summary>");
                    writer.IndentWriteLine("/// " + commentElement.Value);
                    writer.IndentWriteLine("/// </summary>");
                }
            }

            bool memberIsStructure = structures.ContainsKey(csMemberType);
            if (isInterop || !needsMarshalling)
            {
                string member = memberElement.Value;
                string arrayPart = "";
                string fixedPart = "";
                int count = 1;
                if (IsArray(memberElement) && !(memberIsStructure && structures[csMemberType].needsMarshalling))
                {
                    string len = GetArrayLength(memberElement);
                    if (memberIsStructure)
                        count = Convert.ToInt32(len);
                    else if (len != null)
                    {
                        arrayPart = string.Format("[{0}]", len);
                        fixedPart = "unsafe fixed ";
                    }
                }
                if (handles.ContainsKey(csMemberType) && !isPointer)
                {
                    csMemberType = GetHandleType(handles[csMemberType]);
                }
                else
                if ((isPointer && !isInterop && memberIsStructure && structures[csMemberType].needsMarshalling) || csMemberType == "string" || isPointer)
                {
                    csMemberType = "IntPtr";
                }
                for (int i = 0; i < count; i++)
                {
                    writer.IndentWriteLine("{0}{1} {2}{3}{4} {5}{6}{7};", attr, sec, fixedPart, mod, csMemberType, csMemberName, count > 1 ? i.ToString() : "", arrayPart);
                }
            }
            else
            {
                if (isCharArray)
                {
                    WriteMemberCharArray(csMemberName, sec);
                }
                else
                if (isFixedArray)
                {
                    WriteMemberFixedArray(csMemberType, csMemberName, memberElement, memberIsStructure, needsCharCast);
                }
                else
                if (isArray)
                {
                    WriteMemberArray(csMemberType, csMemberName, sec, memberElement);
                    arrayMembers.Add(csMemberName);
                }
                else
                if (memberIsStructure || handles.ContainsKey(csMemberType))
                {
                    WriteMemberStructOrHandle(csMemberType, csMemberName, sec, isPointer);
                }
                else
                if (csMemberType == "string")
                {
                    WriteMemberString(csMemberType, csMemberName, sec);
                }
                else
                if (csMemberType == "string[]")
                {
                    WriteMemberStringArray(csMemberType, csMemberName, sec);
                    arrayMembers.Add(csMemberName);
                }
                else
                {
                    string newKeyWord = WriteMemberNewKeyword(memberElement.Parent, csMemberName);

                    writer.IndentWriteLine("public " + newKeyWord + "{0} {1}", csMemberType, csMemberName);
                    writer.IndentWriteLineBraceOpen();
                    {
                        writer.IndentWriteLine("get {{ return M->{0}; }}", csMemberName);
                        writer.IndentWriteLine("set {{ M->{0} = value; }}", csMemberName);
                    }
                    writer.IndentWriteLineBraceClose();
                }
            }

            return !isInterop && needsMarshalling;
        }

        string WriteMemberNewKeyword(XElement xParent, string csMemberName)
        {
            var newKeyWord = "";

            var isStruct = (isInterop || !needsMarshalling);

            var baseClass = BaseClass(xParent, isStruct);

            if (baseClass != null)
            {
                structures.TryGetValue(baseClass, out StructInfo baseClassInfo);

                if (baseClassInfo?.members != null)
                {
                    if (baseClassInfo.members.ContainsKey(csMemberName))
                    {
                        newKeyWord = "new ";
                    }
                }
                else
                {
                    Console.WriteLine("error: the class '" + baseClass + "' is not generated so far.");
                }
            }

            return newKeyWord;
        }

        void WriteMemberCharArray(string csMemberName, string sec)
        {
            writer.IndentWriteLine("{0} string {1}", sec, csMemberName);
            writer.IndentWriteLineBraceOpen();
            writer.IndentWriteLine("get {{ return Marshal.PtrToStringAnsi ((IntPtr)M->{0}); }}", csMemberName);
            writer.IndentWriteLine("set {{ Interop.Structure.MarshalFixedSizeString (M->{0}, value, 256); }}", csMemberName);
            writer.IndentWriteLineBraceClose();
        }

        static Dictionary<string, string> fieldCounterMap = new Dictionary<string, string>
        {
            { "Code", "CodeSize" },
            { "SampleMask", "RasterizationSamples" },
            { "MemoryTypes", "MemoryTypeCount" },
            { "MemoryHeaps", "MemoryHeapCount" },
            { "AcquireSyncs", "AcquireCount" },
            { "AcquireKeys", "AcquireCount" },
            { "AcquireTimeoutMilliseconds", "AcquireCount" },
            { "ReleaseSyncs", "ReleaseCount" },
            { "ReleaseKeys", "ReleaseCount" },
        };

        void WriteMemberFixedArray(string csMemberType, string csMemberName, XElement memberElement, bool isStruct, bool needsCharCast)
        {
            string counter = fieldCounterMap.ContainsKey(csMemberName) ? fieldCounterMap[csMemberName] : null;
            string len = GetArrayLength(memberElement);
            writer.IndentWriteLine("public {0}[] {1}", csMemberType, csMemberName);
            writer.IndentWriteLineBraceOpen();
            {
                bool isMarshalled = handles.ContainsKey(csMemberType);

                writer.IndentWriteLine("get");
                writer.IndentWriteLineBraceOpen();
                {
                    if (counter != null)
                        len = string.Format("M->{0}", counter);
                    writer.IndentWriteLine("var arr = new {0} [{1}];", csMemberType, len);
                    writer.IndentWriteLine("for (int i = 0; i < {0}; i++)", len);
                    writer.IndentLevel++;
                    if (isStruct)
                    {
                        writer.IndentWriteLine("unsafe");
                        writer.IndentWriteLineBraceOpen();
                        writer.IndentWriteLine("arr [i] = (&M->{0}0) [i];", csMemberName);
                        writer.IndentWriteLineBraceClose();
                    }
                    else
                    if (isMarshalled)
                    {
                        writer.IndentWriteLine("arr [i] = new {0} () {{ M = M->{1} [i] }};", csMemberType, csMemberName);
                    }
                    else
                    {
                        writer.IndentWriteLine("arr [i] = {1}M->{0} [i];", csMemberName, needsCharCast ? "(char)" : "");
                    }
                    writer.IndentLevel--;
                    writer.IndentWriteLine("return arr;");
                }
                writer.IndentWriteLineBraceClose();

                writer.IndentWriteLine("set");
                writer.IndentWriteLineBraceOpen();
                {
                    writer.IndentWriteLine("if (value.Length > {0})", len);
                    writer.IndentLevel++;
                    writer.IndentWriteLine("throw new Exception (\"array too long\");");
                    writer.IndentLevel--;
                    if (counter != null)
                        writer.IndentWriteLine("{0} = (uint)value.Length;", len);
                    writer.IndentWriteLine("for (int i = 0; i < value.Length; i++)");
                    writer.IndentLevel++;
                    if (isStruct)
                    {
                        writer.IndentWriteLine("unsafe");
                        writer.IndentWriteLineBraceOpen();
                        writer.IndentWriteLine("(&M->{0}0) [i] = value [i];", csMemberName);
                        writer.IndentWriteLineBraceClose();
                    }
                    else
                    if (isMarshalled)
                    {
                        writer.IndentWriteLine("M->{0} [i] = value [i].M;", csMemberName);
                    }
                    else
                    {
                        writer.IndentWriteLine("M->{0} [i] = {1}value [i];", csMemberName, needsCharCast ? "(byte)" : "");
                    }
                    writer.IndentLevel--;
                    if (counter == null && !isStruct)
                    {
                        writer.IndentWriteLine("for (int i = value.Length; i < {0}; i++)", len);
                        writer.IndentLevel++;
                        if (isStruct)
                        {
                            writer.IndentWriteLine("unsafe");
                            writer.IndentWriteLineBraceOpen();
                            writer.IndentWriteLine("(&M->{0}0) [i] = 0;", csMemberName);
                            writer.IndentWriteLineBraceClose();
                        }
                        else
                        if (isMarshalled)
                        {
                            writer.IndentWriteLine("M->{0} [i] = IntPtr.Zero;", csMemberName);
                        }
                        else
                        {
                            writer.IndentWriteLine("M->{0} [i] = 0;", csMemberName);
                        }
                        writer.IndentLevel--;
                    }
                }
                writer.IndentWriteLineBraceClose();
            }
            writer.IndentWriteLineBraceClose();
        }

        void WriteMemberArray(string csMemberType, string csMemberName, string sec, XElement memberElement)
        {
            string countName;

            var lenAttribute = memberElement.Attribute("len");
            var structNeedsMarshalling = structures.ContainsKey(csMemberType) && structures[csMemberType].needsMarshalling;
            var isHandle = handles.ContainsKey(csMemberType);
            var ptrType = isHandle ? GetHandleType(handles[csMemberType]) : csMemberType;
            if (fieldCounterMap.ContainsKey(csMemberName))
                countName = fieldCounterMap[csMemberName];
            else if (lenAttribute != null)
            {
                countName = TranslateCName(lenAttribute.Value);
            }
            else
                throw new Exception(string.Format("do not know the counter for {0}", csMemberName));
            // fixme: handle size_t sized arrays better
            string zero, cast, len, lenFromValue;
            if (csMemberName == "Code")
            {
                cast = "(UIntPtr)";
                zero = "UIntPtr.Zero";
                len = string.Format("((uint)M->{0} >> 2)", countName);
                lenFromValue = string.Format("(value.Length << 2)");
            }
            else if (csMemberName == "SampleMask")
            {
                cast = "";
                zero = "0";
                len = string.Format("((uint)M->{0} >> 5)", countName);
                lenFromValue = string.Format("(SampleCountFlags)(value.Length << 5)");
            }
            else
            {
                cast = "(uint)";
                zero = "0";
                len = string.Format("M->{0}", countName);
                lenFromValue = "value.Length";
            }
            writer.IndentWriteLine("NativeReference ref{0};", csMemberName);
            writer.IndentWriteLine("{0} {1}[] {2}", sec, csMemberType, csMemberName);
            writer.IndentWriteLineBraceOpen();
            {
                writer.IndentWriteLine("get");
                writer.IndentWriteLineBraceOpen();
                {
                    writer.IndentWriteLine("if (M->{0} == {1})", countName, zero);
                    writer.IndentLevel++;
                    writer.IndentWriteLine("return null;");
                    writer.IndentLevel--;
                    writer.IndentWriteLine("var values = new {0} [{1}];", csMemberType, len);
                    writer.IndentWriteLine("unsafe");
                    writer.IndentWriteLineBraceOpen();
                    {
                        writer.IndentWriteLine("{0}{1}* ptr = ({0}{1}*)M->{2};", structNeedsMarshalling ? (InteropNamespace + ".") : "", ptrType, csMemberName);
                        writer.IndentWriteLine("for (int i = 0; i < values.Length; i++)");
                        writer.IndentWriteLineBraceOpen();
                        {
                            if (structNeedsMarshalling)
                            {
                                writer.IndentWriteLine("values [i] = new {0} ();", csMemberType);
                                writer.IndentWriteLine("*values [i].M = ptr [i];", csMemberType);
                            }
                            else if (isHandle)
                            {
                                writer.IndentWriteLine("values [i] = new {0} ();", csMemberType);
                                writer.IndentWriteLine("values [i].M = ptr [i];", csMemberType);
                            }
                            else
                                writer.IndentWriteLine("values [i] = ptr [i];");
                        }
                        writer.IndentWriteLineBraceClose();
                    }
                    writer.IndentWriteLineBraceClose();
                    writer.IndentWriteLine("return values;");
                }
                writer.IndentWriteLineBraceClose();

                writer.IndentWriteLine("set");
                writer.IndentWriteLineBraceOpen();
                {
                    writer.IndentWriteLine("if (value == null)");
                    writer.IndentWriteLineBraceOpen();
                    {
                        writer.IndentWriteLine("M->{0} = {1};", countName, zero);
                        writer.IndentWriteLine("M->{0} = IntPtr.Zero;", csMemberName);
                        writer.IndentWriteLine("return;");
                    }
                    writer.IndentWriteLineBraceClose();
                    writer.IndentWriteLine("M->{0} = {1}{2};", countName, cast, lenFromValue);
                    writer.IndentWriteLine("ref{0} = new NativeReference ((int)(sizeof({1}{2}) * value.Length));", csMemberName, structNeedsMarshalling ? (InteropNamespace + ".") : "", ptrType);
                    writer.IndentWriteLine("M->{0} = ref{0}.Handle;", csMemberName);
                    writer.IndentWriteLine("unsafe");
                    writer.IndentWriteLineBraceOpen();
                    {
                        writer.IndentWriteLine("{0}{1}* ptr = ({0}{1}*)M->{2};", structNeedsMarshalling ? (InteropNamespace + ".") : "", ptrType, csMemberName);
                        writer.IndentWriteLine("for (int i = 0; i < value.Length; i++)");
                        writer.IndentWriteLineBraceOpen();
                        {
                            if (structNeedsMarshalling)
                                writer.IndentWriteLine("ptr [i] = *value [i].M;");
                            else
                                writer.IndentWriteLine("ptr [i] = value [i]{0};", isHandle ? ".M" : "");
                        }
                        writer.IndentWriteLineBraceClose();
                    }
                    writer.IndentWriteLineBraceClose();
                }
                writer.IndentWriteLineBraceClose();
            }
            writer.IndentWriteLineBraceClose();
        }

        void WriteMemberStructOrHandle(string csMemberType, string csMemberName, string sec, bool isPointer)
        {
            var isHandle = handles.ContainsKey(csMemberType);

            bool isMarshalled = true;
            if (structures.ContainsKey(csMemberType))
                isMarshalled = structures[csMemberType].needsMarshalling;

            if (isMarshalled)
            {
                writer.IndentWriteLine("{0} l{1};", csMemberType, csMemberName);
                initializeMembers.Add(new StructMemberInfo() { csName = csMemberName, csType = csMemberType, isPointer = isPointer, isHandle = isHandle });
            }
            writer.IndentWriteLine("{0} {1}{2} {3}", sec, csMemberType, (isPointer && !needsMarshalling) ? "?" : "", csMemberName);
            writer.IndentWriteLineBraceOpen();
            {
                if (isMarshalled)
                {
                    writer.IndentWriteLine("get {{ return l{0}; }}", csMemberName);
                    var castType = isHandle ? GetHandleType(handles[csMemberType]) : "IntPtr";
                    if (isPointer || isHandle)
                        writer.IndentWriteLine("set {{ l{0} = value; M->{0} = value != null ? {1}value.M : default{1}; }}", csMemberName, string.Format("({0})", castType));
                    else
                        writer.IndentWriteLine("set {{ l{0} = value; M->{0} = value != null ? *value.M : default({1}.{2}); }}", csMemberName, InteropNamespace, csMemberType);
                }
                else if (isPointer)
                {
                    var vulkanInteropPrefix = InteropNamespace == "Interop" ? "" : "Vulkan.";
                    writer.IndentWriteLine("get {{ return ({0}){1}Interop.Structure.MarshalPointerToObject (M->{2}, typeof ({0})); }}", csMemberType, vulkanInteropPrefix, csMemberName);
                    writer.IndentWriteLine("set {{ M->{0} = {1}Interop.Structure.MarshalObjectToPointer (M->{0}, value); }}", csMemberName, vulkanInteropPrefix);
                }
                else
                {
                    writer.IndentWriteLine("get {{ return M->{0}; }}", csMemberName);
                    writer.IndentWriteLine("set {{ M->{0} = value; }}", csMemberName);
                }
            }
            writer.IndentWriteLineBraceClose();
        }

        void WriteMemberString(string csMemberType, string csMemberName, string sec)
        {
            writer.IndentWriteLine("{0} {1} {2}", sec, csMemberType, csMemberName);
            writer.IndentWriteLineBraceOpen();
            {
                writer.IndentWriteLine("get {{ return Marshal.PtrToStringAnsi (M->{0}); }}", csMemberName);
                writer.IndentWriteLine("set {{ M->{0} = Marshal.StringToHGlobalAnsi (value); }}", csMemberName);
            }
            writer.IndentWriteLineBraceClose();
        }

        void WriteMemberStringArray(string csMemberType, string csMemberName, string sec)
        {
            writer.IndentWriteLine("NativeReference ref{0};", csMemberName);
            writer.IndentWriteLine("{0} {1} {2}", sec, csMemberType, csMemberName);
            writer.IndentWriteLineBraceOpen();
            {
                string countName;
                if (!csMemberName.EndsWith("Names"))
                    throw new Exception(string.Format("unable to handle member {0} {1}", csMemberType, csMemberName));
                countName = csMemberName.Substring(0, csMemberName.Length - 5) + "Count";

                writer.IndentWriteLine("get");
                writer.IndentWriteLineBraceOpen();
                {
                    writer.IndentWriteLine("if (M->{0} == 0)", countName);
                    writer.IndentLevel++;
                    writer.IndentWriteLine("return null;");
                    writer.IndentLevel--;
                    writer.IndentWriteLine("var strings = new string [M->{0}];", countName);
                    writer.IndentWriteLine("unsafe");
                    writer.IndentWriteLineBraceOpen();
                    {
                        writer.IndentWriteLine("void** ptr = (void**)M->{0};", csMemberName);
                        writer.IndentWriteLine("for (int i = 0; i < M->{0}; i++)", countName);
                        writer.IndentLevel++;
                        writer.IndentWriteLine("strings [i] = Marshal.PtrToStringAnsi ((IntPtr)ptr [i]);");
                        writer.IndentLevel--;
                    }
                    writer.IndentWriteLineBraceClose();
                    writer.IndentWriteLine("return strings;");
                }
                writer.IndentWriteLineBraceClose();

                writer.IndentWriteLine("set");
                writer.IndentWriteLineBraceOpen();
                {
                    writer.IndentWriteLine("if (value == null)");
                    writer.IndentWriteLineBraceOpen();
                    {
                        writer.IndentWriteLine("M->{0} = 0;", countName);
                        writer.IndentWriteLine("M->{0} = IntPtr.Zero;", csMemberName);
                        writer.IndentWriteLine("return;");
                    }
                    writer.IndentWriteLineBraceClose();
                    writer.IndentWriteLine("M->{0} = (uint)value.Length;", countName);
                    writer.IndentWriteLine("ref{0} = new NativeReference ((int)(sizeof(IntPtr) * M->{1}));", csMemberName, countName);
                    writer.IndentWriteLine("M->{0} = ref{0}.Handle;", csMemberName, countName);
                    writer.IndentWriteLine("unsafe");
                    writer.IndentWriteLineBraceOpen();
                    {
                        writer.IndentWriteLine("void** ptr = (void**)M->{0};", csMemberName);
                        writer.IndentWriteLine("for (int i = 0; i < M->{0}; i++)", countName);
                        writer.IndentLevel++;
                        writer.IndentWriteLine("ptr [i] = (void*) Marshal.StringToHGlobalAnsi (value [i]);");
                        writer.IndentLevel--;
                    }
                    writer.IndentWriteLineBraceClose();
                }
                writer.IndentWriteLineBraceClose();
            }
            writer.IndentWriteLineBraceClose();
        }

        List<StructMemberInfo> initializeMembers;
        List<string> arrayMembers;

        string InnerValue(XElement element)
        {
            var sb = new StringBuilder();
            foreach (var node in element.Nodes().OfType<XText>())
                sb.Append(node.Value);
            return sb.ToString();
        }

        bool IsArray(XElement memberElement)
        {
            return InnerValue(memberElement).Contains('[');
        }

        void WriteStructureInitialize(List<StructMemberInfo> members, string csName, bool hasSType)
        {
            writer.WriteLine();
            //writer.IndentWriteLine("internal void Initialize ()");
            //writer.IndentWriteLineBraceOpen();
            {
                if (hasSType)
                {
                    var commentOut = csName == "BaseOutStructure" || csName == "BaseInStructure" ? "// " : "";
                    writer.IndentWriteLine(commentOut + "M->SType = StructureType.{0};", csName);
                }

                foreach (var info in members)
                    if (handles.ContainsKey(info.csType) || (structures.ContainsKey(info.csType) && structures[info.csType].needsMarshalling))
                        if (!info.isPointer && !info.isHandle)
                            writer.IndentWriteLine("l{0} = new {1} (new NativePointer (native.Reference, (IntPtr)(&M->{0})));", info.csName, info.csType);
            }
            //writer.IndentWriteLineBraceClose();
            //writer.WriteLine("");
        }

        string GetAPIConstant(string name)
        {
            var constants = xSpecTree.Elements("enums").FirstOrDefault(e => e.Attribute("name").Value == "API Constants");
            if (constants == null)
                return null;
            var field = constants.Elements("enum").FirstOrDefault(e => e.Attribute("name").Value == name);
            if (field == null)
                return null;
            return field.Attribute("value").Value;
        }

        string GetArrayLength(XElement member)
        {
            var enumElement = member.Element("enum");
            if (enumElement != null)
                return GetAPIConstant(enumElement.Value);
            string len = member.Value.Substring(member.Value.IndexOf('[') + 1);
            return len.Substring(0, len.IndexOf(']'));
        }

        #endregion

        #region GenerateFileHandlesCs

        void Generate(string typeCategory, Func<XElement, bool> generator)
        {
            var elements = xSpecTree.Elements("types").Elements("type").Where(e => e.Attribute("category")?.Value == typeCategory).ToList();

            GenerateCodeForElements(elements, generator);
        }

        void GenerateCodeForElements(IEnumerable<XElement> elements, Func<XElement, bool> generator)
        {
            bool prependNewLine = false;
            foreach (var e in elements)
            {
                if (prependNewLine)
                    writer.WriteLine();

                prependNewLine = generator(e);
            }
        }

        void LearnHandles()
        {
            var typesWithCategory = xSpecTree.Elements("types").Elements("type").Where(e => e.Attribute("category") != null).ToList();

            var xHandleTypes1 = typesWithCategory.Where(t => t.Attribute("category").Value == "handle").Where(t => t.Attribute("alias") == null).ToList();
            var xHandleTypes2 = typesWithCategory.Where(t => t.Attribute("category").Value == "handle").Where(t => t.Attribute("alias") != null).ToList();
            foreach (var xHandleType in xHandleTypes1)
            {
                var xAliases = xHandleTypes2.Where(a => a.Attribute("alias").Value == xHandleType.Element("name").Value).ToList();

                LearnHandle(xHandleType, xAliases);
            }
        }

        bool LearnHandle(XElement xHandleType, List<XElement> xAliases)
        {
            if (xHandleType.Element("name") != null)
            {
                string name = xHandleType.Element("name").Value;
                string csName = GetTypeCsName(name, "struct");
                string type = xHandleType.Element("type").Value;

                handles.Add(csName, new HandleInfo { name = csName, type = type });

                return false;
            }
            else
            {
                string name = xHandleType.Attribute("name").Value;
                string csName = GetTypeCsName(name, "struct");

                string alias = xHandleType.Attribute("alias").Value;
                string csNameA = GetTypeCsName(alias, "struct");

                handles.Add(csName, handles[csNameA]);

                return false;
            }
        }

        void GenerateFileHandlesCs()
        {
            writer = new Writer(Path.Combine(outputDir, "Handles.cs"), UsingNamespaceFlags.Interop | UsingNamespaceFlags.Collections, Namespace);
            Generate("handle", WriteHandle);
            writer.FinalizeFile();
            writer = null;
        }

        bool WriteHandle(XElement handleElement)
        {
            if (handleElement.Attribute("alias") != null)
                return false;

            string csName = GetTypeCsName(handleElement.Element("name").Value, "handle");
            HandleInfo info = handles[csName];
            bool isRequired = false;

            if (platformExtensionsRequiredCommands != null)
            {
                foreach (var commandElement in info.commands)
                {
                    if (platformExtensionsRequiredCommands.Contains(commandElement.Element("proto").Element("name").Value))
                    {
                        isRequired = true;
                        break;
                    }
                }

                if (!isRequired)
                    return false;
            }

            var className = string.Format("{0}{1}", csName, isRequired ? "Extension" : "");
            var marshallingInterface = info.type == "VK_DEFINE_NON_DISPATCHABLE_HANDLE" ? "INonDispatchableHandleMarshalling" : "IMarshalling";
            writer.IndentWriteLine("public {0} class {1}{2}", isRequired ? "static" : "partial", className, isRequired ? "" : string.Format(" : {0}", marshallingInterface));
            writer.IndentWriteLineBraceOpen();
            {
                if (!isRequired && !handlesWithDefaultConstructors.Contains(csName))
                {
                    writer.IndentWriteLine("internal {0}() {{ }}", className);
                    writer.WriteLine("");
                }
                //// todo: implement marshalling
                bool prependNewLine = false;
                if (platformExtensionsRequiredCommands == null)
                {
                    var handleType = GetHandleType(info);
                    writer.IndentWriteLine("internal {0} M;", handleType);
                    writer.IndentWriteLine("{0} {1}.Handle", handleType, marshallingInterface);
                    writer.IndentWriteLineBraceOpen();
                    {
                        writer.IndentWriteLine("get");
                        writer.IndentWriteLineBraceOpen();
                        {
                            writer.IndentWriteLine("return M;");
                        }
                        writer.IndentWriteLineBraceClose();
                    }
                    writer.IndentWriteLineBraceClose();

                    prependNewLine = true;
                }

                if (info.commands.Count > 0)
                {
                    foreach (var element in info.commands)
                    {
                        prependNewLine = WriteCommand(element, prependNewLine, true, true, csName, isRequired);
                    }
                }
            }
            writer.IndentWriteLineBraceClose();

            return true;
        }

        HashSet<string> handlesWithDefaultConstructors = new HashSet<string> { "Instance" };

        string GetHandleType(HandleInfo info)
        {
            switch (info.type)
            {
                case "VK_DEFINE_NON_DISPATCHABLE_HANDLE":
                    return "UInt64";
                case "VK_DEFINE_HANDLE":
                    return "IntPtr";
                default:
                    throw new Exception("unknown handle type: " + info.type);
            }
        }

        #endregion

        #region GenerateFileImportedCommandsCs

        void GenerateFileImportedCommandsCs()
        {
            writer = new Writer(Path.Combine(outputDir, "Interop", "ImportedCommands.cs"), UsingNamespaceFlags.Interop | (platformExtensionsRequiredCommands == null ? 0 : UsingNamespaceFlags.VulkanInterop), "Vulkan." + InteropNamespace);

            writer.IndentWriteLine("internal static class NativeMethods");
            writer.IndentWriteLineBraceOpen();
            {
                writer.IndentWriteLine("const string VulkanLibrary = \"{0}\";", (platform == null || platform == "Windows") ? "vulkan-1" : (platform == "iOS" ? "__Internal" : "vulkan"));
                writer.WriteLine("");

                bool prependNewLine = false;
                foreach (var command in xSpecTree.Elements("commands").Elements("command"))
                {
                    WriteUnmanagedCommand(ref prependNewLine, command);
                }
            }
            writer.IndentWriteLineBraceClose();

            writer.FinalizeFile();
            writer = null;
        }

        void WriteUnmanagedCommand(ref bool prependNewLine, XElement commandElement)
        {
            if (commandElement.Attribute("alias") != null)
                return;

            string function = commandElement.Element("proto").Element("name").Value;
            string type = commandElement.Element("proto").Element("type").Value;
            string csType = GetEnumCsName(type, true);

            // todo: extensions support
            if (platformExtensionsRequiredCommands != null)
            {
                if (!platformExtensionsRequiredCommands.Contains(function))
                    return;
            }
            else
            if (disabledUnmanagedCommands.Contains(function))
            {
                return;
            }

            if (prependNewLine)
            {
                writer.WriteLine();
                prependNewLine = false;
            }

            // todo: function pointers
            if (csType.StartsWith("PFN_"))
                csType = "IntPtr";

            if (delegateUnmanagedCommands.Contains(function))
                writer.IndentWrite("internal unsafe delegate {0} {1} (", csType, function);
            else
            {
                writer.IndentWriteLine("[DllImport (VulkanLibrary, CallingConvention = CallingConvention.Winapi)]");
                writer.IndentWrite("internal static unsafe extern {0} {1} (", csType, function);
            }
            WriteUnmanagedCommandParameters(commandElement);
            writer.WriteLine(");");

            prependNewLine = true;
            return;
        }

        HashSet<string> disabledUnmanagedCommands = new HashSet<string>
        {
            "vkGetPhysicalDeviceXcbPresentationSupportKHR",
            "vkCreateMirSurfaceKHR",
            "vkGetPhysicalDeviceMirPresentationSupportKHR",
            "vkCreateWaylandSurfaceKHR",
            "vkGetPhysicalDeviceWaylandPresentationSupportKHR",
            "vkCreateWin32SurfaceKHR",
            "vkGetPhysicalDeviceWin32PresentationSupportKHR",
            "vkCreateXlibSurfaceKHR",
            "vkGetPhysicalDeviceXlibPresentationSupportKHR",
            "vkCreateXcbSurfaceKHR",
            "vkCreateAndroidSurfaceKHR",
            "vkGetMemoryWin32HandleNV",
            "vkImportSemaphoreWin32HandleKHR",
            "vkImportFenceWin32HandleKHR",
            "vkCreateIOSSurfaceMVK",
			// TODO: support fixed array of Handles
			"vkEnumeratePhysicalDeviceGroups",
			// TODO: support fixed array of Handles
			"vkEnumeratePhysicalDeviceGroupsKHX",
            "vkCreateRaytracingPipelinesNVX",
            "vkGetImageDrmFormatModifierPropertiesEXT",
            // TODO: Pointer to IntPtr-buffer
            "vkGetAndroidHardwareBufferPropertiesANDROID",
            // TODO: PointerPointer to IntPtr-buffer
            "vkGetMemoryAndroidHardwareBufferANDROID",
            "vkCreateImagePipeSurfaceFUCHSIA",
        };

        HashSet<string> delegateUnmanagedCommands = new HashSet<string>
        {
            "vkCreateDebugReportCallbackEXT",
            "vkDestroyDebugReportCallbackEXT",
            "vkDebugReportMessageEXT",
        };

        void WriteUnmanagedCommandParameters(XElement commandElement)
        {
            bool first = true;
            bool previous = false;
            foreach (var param in commandElement.Elements("param"))
            {
                string type = param.Element("type").Value;
                string name = param.Element("name").Value;
                string csType = GetEnumCsName(type, true);

                bool isPointer = param.Value.Contains(type + "*");
                if (handles.ContainsKey(csType))
                {
                    var handle = handles[csType];
                    if (first && !isPointer)
                        handle.commands.Add(commandElement);
                    csType = handle.type == "VK_DEFINE_HANDLE" ? "IntPtr" : "UInt64";
                }
                bool isStruct = structures.ContainsKey(csType);
                bool isRequired = platformExtensionsRequiredTypes != null && platformExtensionsRequiredTypes.Contains(type);
                if (isPointer)
                {
                    switch (csType)
                    {
                        case "void":
                            csType = "IntPtr";
                            break;
                        case "char":
                            csType = "string";
                            isPointer = false;
                            break;
                        default:
                            csType += "*";
                            break;
                    }
                }
                else if (first && handles.ContainsKey(csType))
                    handles[csType].commands.Add(commandElement);

                name = GetParamName(param);

                if (previous)
                    writer.Write(", ");
                else
                    previous = true;

                if (param.Value.Contains(type + "**"))
                    csType += "*";

                writer.Write("{0}{1} {2}", (isStruct && platformExtensionsRequiredCommands != null && !isRequired) ? "Vulkan.Interop." : "", csType, keywords.Contains(name) ? "@" + name : name);
                first = false;
            }
        }

        #endregion

        #region GenerateFileCommandsCs

        void GenerateFileCommandsCs()
        {
            writer = new Writer(Path.Combine(outputDir, "Commands.cs"), UsingNamespaceFlags.Interop | UsingNamespaceFlags.Collections);

            writer.IndentWriteLine("public static partial class Commands");
            writer.IndentWriteLineBraceOpen();

            var handlesCommands = new HashSet<string>();
            foreach (var handle in handles)
                foreach (var command in handle.Value.commands)
                    handlesCommands.Add(command.Element("proto").Element("name").Value);

            bool prependNewLine = false;
            foreach (var command in xSpecTree.Elements("commands").Elements("command"))
            {
                if (command.Attribute("alias") != null)
                    continue;

                if (handlesCommands.Contains(command.Element("proto").Element("name").Value))
                    continue;

                prependNewLine = WriteCommand(command, prependNewLine, true);
            }

            writer.IndentWriteLineBraceClose();

            writer.FinalizeFile();
            writer = null;
        }

        bool WriteCommand(XElement commandElement, bool prependNewLine, bool useArrayParameters, bool isForHandle = false, string handleName = null, bool isExtension = false)
        {
            string function = commandElement.Element("proto").Element("name").Value;
            string type = commandElement.Element("proto").Element("type").Value;
            string csType = GetTypeCsName(type);
            bool hasArrayParameter = false;

            // todo: extensions support
            if (platformExtensionsRequiredCommands != null)
            {
                if (!platformExtensionsRequiredCommands.Contains(function))
                    return false;
            }
            else
            if (disabledUnmanagedCommands.Contains(function) || disabledCommands.Contains(function))
            {
                return false;
            }

            if (prependNewLine)
                writer.WriteLine();

            // todo: function pointers
            if (csType.StartsWith("PFN_"))
                csType = "IntPtr";

            string csFunction = function;
            if (function.StartsWith("vk"))
                csFunction = csFunction.Substring(2);

            if (isForHandle)
            {
                if (csFunction.StartsWith(handleName))
                    csFunction = csFunction.Substring(handleName.Length);
                else if (csFunction.StartsWith("Get" + handleName))
                    csFunction = "Get" + csFunction.Substring(handleName.Length + 3);
                else if (csFunction.EndsWith(handleName))
                    csFunction = csFunction.Substring(0, csFunction.Length - handleName.Length);
            }

            if (!useArrayParameters && csFunction.EndsWith("s"))
                csFunction = csFunction.Substring(0, csFunction.Length - 1);

            int fixedCount, outCount;
            var paramsDict = LearnParams(commandElement, isForHandle && !isExtension, out fixedCount, out outCount);

            var hasResult = csType == "Result";
            if (hasResult)
                csType = "void";

            ParamInfo firstOutParam = null;
            ParamInfo intParam = null;
            string outLen = null;
            ParamInfo dataParam = null;
            var ignoredParameters = new List<ParamInfo>();
            bool createArray = false;
            bool hasLen = false;
            if (csType == "void")
            {
                if (outCount == 1)
                {
                    foreach (var param in paramsDict)
                    {
                        if (param.Value.isOut)
                        {
                            firstOutParam = param.Value;
                            switch (firstOutParam.csType)
                            {
                                case "Bool32":
                                case "IntPtr":
                                case "int":
                                case "Int32":
                                case "UInt32":
                                case "Int64":
                                case "UInt64":
                                case "DeviceSize":
                                    firstOutParam.isFixed = false;
                                    break;
                            }
                            ignoredParameters.Add(param.Value);
                            break;
                        }
                    }
                    csType = firstOutParam.csType;
                    if (csType != "IntPtr" && firstOutParam.len != null /* && paramsDict.ContainsKey (firstOutParam.len) */)
                    {
                        csType += "[]";
                        createArray = true;
                        hasLen = true;
                        intParam = paramsDict.ContainsKey(firstOutParam.len) ? paramsDict[firstOutParam.len] : GetLenParamInfo(firstOutParam.len);
                        dataParam = firstOutParam;
                        intParam.isFixed = false;
                        dataParam.isFixed = false;
                    }
                }
                else
                if (outCount > 1)
                {
                    createArray = CommandShouldCreateArray(commandElement, paramsDict, ref intParam, ref dataParam);
                    if (createArray)
                    {
                        ignoredParameters.Add(intParam);
                        ignoredParameters.Add(dataParam);
                        intParam.isFixed = false;
                        dataParam.isFixed = false;
                        csType = string.Format("{0}[]", dataParam.csType);
                    }
                }
            }

            if (intParam != null)
                outLen = intParam.csName;

            int arrayParamCount = 0;
            foreach (var param in paramsDict)
            {
                var info = param.Value;
                if (info.len != null && paramsDict.ContainsKey(info.len) && !info.isOut && info.csType != "IntPtr")
                {
                    var lenParameter = paramsDict[info.len];
                    ignoredParameters.Add(lenParameter);
                    lenParameter.lenArray = info;
                    if (useArrayParameters)
                    {
                        info.isArray = true;
                        if (lenParameter.csName == outLen)
                            outLen = string.Format("{0}.Length", info.csName);
                        info.isFixed = false;
                        arrayParamCount++;
                    }
                    else
                    {
                        lenParameter.constValue = "1";
                        if (info.isStruct && !info.needsMarshalling)
                            info.isNullable = true;
                        else
                            switch (info.csType)
                            {
                                case "UInt32":
                                case "Int32":
                                case "UInt64":
                                case "Int64":
                                    info.isNullable = true;
                                    break;
                            }
                    }
                }
            }

            if (intParam != null)
                intParam.isOut = false;
            if (dataParam != null)
                dataParam.isOut = false;

            writer.IndentWrite("public {0}{1} {2} (", (!isExtension && isForHandle) ? "" : "static ", csType, csFunction);
            hasArrayParameter = WriteCommandParameters(commandElement, useArrayParameters, ignoredParameters, null, null, isForHandle && !isExtension, false, paramsDict, isExtension, true);
            writer.WriteLine(")");
            writer.IndentWriteLineBraceOpen();
            {
                if (hasResult)
                    writer.IndentWriteLine("Result result;");
                if (firstOutParam != null && !hasLen)
                    writer.IndentWriteLine("{0} {1};", csType, firstOutParam.csName);
                writer.IndentWriteLine("unsafe");
                writer.IndentWriteLineBraceOpen();
                {
                    bool isInInterop = false;
                    if (createArray)
                    {
                        isInInterop = dataParam.isStruct && dataParam.needsMarshalling;
                        if (!hasLen)
                        {
                            writer.IndentWriteLine("UInt32 {0};", outLen);
                            writer.IndentWrite("{0}{1}{2}{3} (", hasResult ? "result = " : "", (ignoredParameters.Count == 0 && csType != "void") ? "return " : "", delegateUnmanagedCommands.Contains(function) ? "" : "Interop.NativeMethods.", function);
                            WriteCommandParameters(commandElement, useArrayParameters, null, dataParam, null, isForHandle && !isExtension, true, paramsDict, isExtension);
                            writer.WriteLine(");");
                            CommandHandleResult(hasResult);
                        }
                        writer.IndentWriteLine("if ({0} <= 0)", outLen);
                        writer.IndentLevel++;
                        writer.IndentWriteLine("return null;");
                        writer.IndentLevel--;
                        writer.WriteLine();
                        writer.IndentWriteLine("int size = {0};", dataParam.MarshalSizeSource(this, isInInterop));
                        writer.IndentWriteLine("var ref{0} = new NativeReference ((int)(size * {1}));", dataParam.csName, outLen);
                        writer.IndentWriteLine("var ptr{0} = ref{0}.Handle;", dataParam.csName);
                    }

                    if (fixedCount > 0)
                    {
                        int count = 0;
                        foreach (var param in paramsDict)
                        {
                            if (param.Value.isFixed && param.Value.isHandle && !param.Value.isConst)
                            {
                                writer.IndentWriteLine("{0} = new {1} ();", param.Key, param.Value.csType);
                                count++;
                            }
                        }
                        if (count > 0)
                            writer.WriteLine();

                        foreach (var param in paramsDict)
                        {
                            if (param.Value.isFixed)
                            {
                                writer.IndentWriteLine("fixed ({0}* ptr{1} = &{1}{2})", GetManagedType(param.Value.csType), GetParamName(param.Key, useArrayParameters), param.Value.isHandle ? ".M" : "");
                                writer.IndentWriteLineBraceOpen();
                            }
                        }
                    }
                    {
                        if (outCount > 0)
                        {
                            foreach (var param in paramsDict)
                            {
                                var info = param.Value;
                                if (info.isOut && !info.isFixed) // && (ignoredParameters == null || !ignoredParameters.Contains (info)))
                                    writer.IndentWriteLine("{0} = new {1} ();", param.Key, info.csType);
                            }
                        }
                        if (arrayParamCount > 0)
                        {
                            foreach (var param in paramsDict)
                            {
                                var info = param.Value;
                                if (info.len != null && firstOutParam != info)
                                {
                                    writer.IndentWriteLine("var array{0} = {0} == null ? IntPtr.Zero : Marshal.AllocHGlobal ({0}.Length * sizeof ({1}));", info.csName, GetParamArrayType(info));
                                    writer.IndentWriteLine("var len{0} = {0} == null ? 0 : {0}.Length;", info.csName);
                                    writer.IndentWriteLine("if ({0} != null)", info.csName);
                                    writer.IndentLevel++;
                                    writer.IndentWriteLine("for (int i = 0; i < {0}.Length; i++)", info.csName);
                                    writer.IndentLevel++;
                                    writer.IndentWriteLine("(({0}*)array{1}) [i] = {3}({1} [i]{2});", GetParamArrayType(info), info.csName, ((info.isStruct && info.needsMarshalling) || info.isHandle) ? ".M" : "", (info.isStruct && info.needsMarshalling) ? "*" : "");
                                    writer.IndentLevel--;
                                    writer.IndentLevel--;
                                }
                            }
                        }
                        foreach (var param in paramsDict)
                        {
                            var info = param.Value;
                            if (info.isNullable)
                            {
                                string name = GetParamName(info.csName, useArrayParameters);
                                writer.IndentWriteLine("{0} val{1} = {1} ?? default({0});", info.csType, name);
                                writer.IndentWriteLine("{0}* ptr{1} = {1} != null ? &val{1} : ({0}*)IntPtr.Zero;", info.csType, name);
                            }
                        }

                        writer.IndentWrite("{0}{1}{2}{3} (", hasResult ? "result = " : "", (ignoredParameters.Count == 0 && csType != "void") ? "return " : "", delegateUnmanagedCommands.Contains(function) ? "" : string.Format("{0}.NativeMethods.", InteropNamespace), function);
                        WriteCommandParameters(commandElement, useArrayParameters, null, null, dataParam, isForHandle && !isExtension, true, paramsDict, isExtension);
                        writer.WriteLine(");");
                    }
                    if (fixedCount > 0)
                    {
                        foreach (var param in paramsDict)
                        {
                            if (param.Value.isFixed)
                            {
                                writer.IndentWriteLineBraceClose();
                            }
                        }
                    }

                    if (arrayParamCount > 0)
                    {
                        foreach (var param in paramsDict)
                        {
                            var info = param.Value;
                            if (info.len != null && firstOutParam != info)
                                writer.IndentWriteLine("Marshal.FreeHGlobal (array{0});", info.csName);
                        }
                    }
                    CommandHandleResult(hasResult);
                    if (firstOutParam != null && !createArray)
                    {
                        writer.WriteLine();
                        writer.IndentWriteLine("return {0};", firstOutParam.csName);
                    }
                    else if (createArray)
                    {
                        writer.WriteLine();
                        writer.IndentWriteLine("if ({0} <= 0)", outLen);
                        writer.IndentLevel++;
                        writer.IndentWriteLine("return null;");
                        writer.IndentLevel--;
                        writer.IndentWriteLine("var arr = new {0} [{1}];", dataParam.csType, outLen);
                        writer.IndentWriteLine("for (int i = 0; i < {0}; i++)", outLen);
                        writer.IndentWriteLineBraceOpen();
                        {
                            if (isInInterop || !dataParam.isStruct)
                            {
                                if (dataParam.isStruct)
                                    writer.IndentWriteLine("arr [i] = new {0} (new NativePointer (ref{4}, (IntPtr)({1}(({2}{3}*)ptr{4}) [i])));", dataParam.csType, dataParam.isStruct ? "&" : "", isInInterop ? "Interop." : "", dataParam.isHandle ? GetHandleType(handles[dataParam.csType]) : dataParam.csType, dataParam.csName);
                                else
                                {
                                    writer.IndentWriteLine("arr [i] = new {0} ();", dataParam.csType);
                                    writer.IndentWriteLine("arr [i]{0} = {1}(({2}{3}*)ptr{4}) [i];", (dataParam.isStruct || dataParam.isHandle) ? ".M" : "", dataParam.isStruct ? "&" : "", isInInterop ? "Interop." : "", dataParam.isHandle ? GetHandleType(handles[dataParam.csType]) : dataParam.csType, dataParam.csName);
                                }
                            }
                            else
                                writer.IndentWriteLine("arr [i] = ((({0}*)ptr{1}) [i]);", dataParam.csType, dataParam.csName);
                        }
                        writer.IndentWriteLineBraceClose();
                        writer.WriteLine();
                        writer.IndentWriteLine("return arr;");
                    }
                }
                writer.IndentWriteLineBraceClose();
            }
            writer.IndentWriteLineBraceClose();

            if (useArrayParameters && hasArrayParameter && !ignoredSimplifiedCommands.Contains(csFunction))
                return WriteCommand(commandElement, true, false, isForHandle, handleName, isExtension);

            return true;
        }

        HashSet<string> disabledCommands = new HashSet<string>
        {
            "vkCreateInstance"
        };
        HashSet<string> ignoredSimplifiedCommands = new HashSet<string>
        {
            "CreateGraphicsPipelines",
            "CreateComputePipelines",
            "CreateSharedSwapchainsKHR",
        };
        HashSet<string> notLengthTypes = new HashSet<string>
        {
            "RROutput",
        };

        Dictionary<string, ParamInfo> LearnParams(XElement commandElement, bool isInstance, out int fixedCount, out int outCount, bool passToNative = false)
        {
            bool first = true;
            var paramsDict = new Dictionary<string, ParamInfo>();

            fixedCount = 0;
            outCount = 0;
            foreach (var param in commandElement.Elements("param"))
            {
                if (first && isInstance)
                {
                    first = false;
                    continue;
                }
                var csName = GetParamName(param);
                var info = new ParamInfo() { csName = csName };
                var lenAttr = param.Attribute("len");
                if (lenAttr != null)
                    info.len = lenAttr.Value;
                string type = param.Element("type").Value;
                bool isPointer = param.Value.Contains(type + "*");
                info.csType = GetParamCsType(type, ref isPointer, out info.isHandle);
                info.type = type;
                paramsDict.Add(csName, info);
                info.isPointer = isPointer;
                info.isConst = info.isPointer && param.Value.Contains("const ");
                if (info.isPointer && !info.isConst)
                {
                    info.isOut = true;
                    outCount++;
                }
                info.isStruct = structures.ContainsKey(info.csType);
                if (info.isStruct)
                {
                    info.needsMarshalling = (structures[info.csType].needsMarshalling || !isPointer);
                }
                if (isPointer && type == "void" && !param.Value.Contains("**"))
                    continue;
                if (!isPointer || info.isStruct)
                    continue;
                if (info.isHandle || !param.Value.Contains("const ") && !enumInfos.ContainsKey(info.csType))
                {
                    info.isFixed = true;
                    fixedCount++;
                }
            }

            return paramsDict;
        }

        string GetParamName(XElement param)
        {
            var name = param.Element("name").Value;
            if (param.Value.Contains(param.Element("name").Value + "*") && name.StartsWith("p"))
                name = name.Substring(1);

            return name;
        }

        string GetParamCsType(string type, ref bool isPointer, out bool isHandle)
        {
            string csType = GetEnumCsName(type, true);
            isHandle = handles.ContainsKey(csType);

            if (!isPointer)
                return csType;

            if (!isHandle)
            {
                switch (csType)
                {
                    case "void":
                        csType = "IntPtr";
                        break;
                    case "char":
                        csType = "string";
                        isPointer = false;
                        break;
                }
            }

            return csType;
        }

        ParamInfo GetLenParamInfo(string len)
        {
            int index = len.IndexOf("->");
            if (index < 0)
                index = len.IndexOf("::");
            if (index > 0)
                len = string.Format("{0}.{1}", len.Substring(0, index), TranslateCName(len.Substring(index + 2)));

            return new ParamInfo { csName = len };
        }

        string GetParamArrayType(ParamInfo info)
        {

            if (info.isHandle)
                return GetHandleType(handles[info.csType]);
            if (info.isStruct && info.needsMarshalling)
                return string.Format("{0}.{1}", InteropNamespace, info.csType);

            return info.csType;
        }

        bool CommandShouldCreateArray(XElement commandElement, Dictionary<string, ParamInfo> paramsDict, ref ParamInfo intParam, ref ParamInfo dataParam)
        {
            ParamInfo outUInt = null;
            foreach (var param in commandElement.Elements("param"))
            {
                string name = GetParamName(param);
                if (!paramsDict.ContainsKey(name))
                    continue;

                var info = paramsDict[name];
                if (info.csType == "UInt32" && !notLengthTypes.Contains(info.type))
                    outUInt = info;
                else
                {
                    if (outUInt != null && info.isOut && (info.isStruct || info.isHandle || info.isPointer))
                    {
                        intParam = outUInt;
                        dataParam = info;

                        return true;
                    }
                    outUInt = null;
                }
            }

            return false;
        }

        void CommandHandleResult(bool hasResult)
        {
            if (hasResult)
            {
                writer.IndentWriteLine("if (result != Result.Success)");
                writer.IndentLevel++;
                writer.IndentWriteLine("throw new ResultException (result);");
                writer.IndentLevel--;
            }
        }

        string GetManagedType(string csType)
        {
            if (structures.ContainsKey(csType))
                return "IntPtr";

            if (handles.ContainsKey(csType))
                return GetManagedHandleType(handles[csType].type);

            switch (csType)
            {
                case "void":
                    return "IntPtr";
            }

            return csType;
        }

        string GetManagedHandleType(string handleType)
        {
            return handleType == "VK_DEFINE_HANDLE" ? "IntPtr" : "UInt64";
        }

        bool WriteCommandParameters(XElement commandElement, bool useArrayParameters, List<ParamInfo> ignoredParameters = null, ParamInfo nullParameter = null, ParamInfo ptrParam = null, bool isInstance = false, bool passToNative = false, Dictionary<string, ParamInfo> paramsDict = null, bool isExtension = false, bool useOptional = false)
        {
            bool first = true;
            bool previous = false;
            string firstOptional = null;
            bool hasArrayParameter = false;

            if (useOptional)
            {
                foreach (var param in commandElement.Elements("param"))
                {
                    string name = GetParamName(param);
                    if (!paramsDict.ContainsKey(name))
                        continue;

                    var info = paramsDict[name];

                    if (ignoredParameters != null && ignoredParameters.Contains(info))
                        continue;

                    var optional = param.Attribute("optional");
                    bool isOptionalParam = (optional != null && optional.Value == "true");

                    if (!isOptionalParam)
                        firstOptional = null;
                    else if (firstOptional == null)
                        firstOptional = name;
                }
            }
            var optionalPart = false;
            foreach (var param in commandElement.Elements("param"))
            {
                string name = GetParamName(param);

                if (first)
                {
                    first = false;
                    if (passToNative && (isInstance || isExtension))
                    {
                        writer.Write("{0}.M", isExtension ? name : "this");
                        previous = true;
                        continue;
                    }
                    if (isInstance)
                        continue;
                    if (isExtension)
                        writer.Write("this ");
                }

                string type = param.Element("type").Value;
                var info = paramsDict[name];

                if (ignoredParameters != null && ignoredParameters.Contains(info))
                    continue;

                if (info.isArray)
                    hasArrayParameter = true;

                var optional = param.Attribute("optional");
                bool isOptionalParam = (optional != null && optional.Value == "true");
                bool isDoublePointer = param.Value.Contains(type + "**");
                if (!isDoublePointer && info.isPointer && info.len != null && info.csType == "IntPtr")
                {
                    info.isPointer = false;
                    info.isOut = false;
                }

                if (previous)
                    writer.Write(", ");
                else
                    previous = true;

                if (passToNative)
                {
                    name = GetParamName(name, useArrayParameters);
                    string paramName = info.isFixed ? "ptr" + name : name;
                    bool useHandlePtr = !info.isFixed && ((info.isStruct && info.needsMarshalling) || info.isHandle);

                    if (info == nullParameter)
                        writer.Write("null");
                    else if (useArrayParameters && info.isArray)
                    {
                        var arrayType = GetParamArrayType(info);
                        writer.Write("({0}*)array{1}", arrayType, info.csName);
                    }
                    else if (info == ptrParam)
                        writer.Write("({0}{1}*)ptr{2}", (info.isStruct && info.needsMarshalling) ? "Interop." : "", info.isHandle ? GetHandleType(handles[info.csType]) : info.csType, GetParamName(info.csName, useArrayParameters));
                    else if (isOptionalParam && info.isPointer && !info.isOut)
                        writer.Write("{0} != null ? {0}{1} : null", GetSafeParameterName(paramName), useHandlePtr ? ".M" : "");
                    else if (info.lenArray != null)
                    {
                        if (useArrayParameters)
                            writer.Write("(uint)len{0}", info.lenArray.csName);
                        else
                            writer.Write("({0})({1} != null ? {2} : 0)", info.csType, GetParamName(info.lenArray.csName, useArrayParameters), info.constValue);
                    }
                    else
                    {
                        var safeParamName = GetSafeParameterName(paramName);
                        var needsAddress = (info.isPointer && (!info.isStruct || !info.needsMarshalling) && !(info.isConst && info.csType == "IntPtr") && !info.isFixed);
                        var nativeValue = GetDefaultNativeValue(info.csType);
                        if (useHandlePtr)
                        {
                            if (!needsAddress && nativeValue == "null")
                                writer.Write("{0}?.M", safeParamName);
                            else
                                writer.Write("{0} != null ? {1}{0}.M : {2}", safeParamName, needsAddress ? "&" : "", nativeValue);
                        }
                        else
                            writer.Write("{0}{1}", (needsAddress && !info.isNullable) ? "&" : "", info.isNullable ? string.Format("ptr{0}", safeParamName) : safeParamName);
                    }
                }
                else
                {
                    if (firstOptional == name)
                        optionalPart = true;
                    name = GetParamName(name, useArrayParameters);
                    writer.Write("{0}{1}{2}{3} {4}{5}", info.isOut ? "out " : "", info.csType, info.isNullable ? "?" : "", (useArrayParameters && info.isArray) ? "[]" : "", keywords.Contains(name) ? "@" + name : name, (optionalPart && isOptionalParam) ? string.Format(" = {0}", GetDefaultValue(info.csType)) : "");
                }
            }

            return hasArrayParameter;
        }

        string GetDefaultValue(string csType)
        {
            // Check for any known extensions and remove them
            String internalCopy = csType;
            string suffix = null;
            foreach (var ext in vendorTags)
            {
                if (internalCopy.EndsWith(ext.Value.csName))
                {
                    suffix = ext.Value.csName + suffix;
                    internalCopy = internalCopy.Substring(0, csType.Length - ext.Value.csName.Length);
                }
            }

            if (internalCopy.EndsWith("Flags"))
                return string.Format("({0})0", csType);

            // other known types
            switch (csType)
            {
                case "IntPtr":
                    return "default(IntPtr)";
                case "UInt32":
                    return "0";
            }

            return "null";
        }

        string GetDefaultNativeValue(string csType)
        {
            if (csType.EndsWith("Flags"))
                return string.Format("({0})0", csType);

            if (handles.ContainsKey(csType))
                csType = GetHandleType(handles[csType]);
            else if (structures.ContainsKey(csType) && structures[csType].needsMarshalling)
                return string.Format("({0}.{1}*)default(IntPtr)", InteropNamespace, csType);

            return string.Format("default({0})", csType);
        }

        string GetParamName(string name, bool useArrayParameters)
        {
            if (!useArrayParameters && name.EndsWith("s"))
                return name.Substring(0, name.Length - 1);
            return name;
        }

        string GetSafeParameterName(string paramName)
        {
            // if paramName is a reserved name
            return keywords.Contains(paramName) ? "@" + paramName : paramName;
        }

        HashSet<string> keywords = new HashSet<string>
        {
            "event",
            "object",
        };

        #endregion

        #region GeneratePlatformExtensions

        string platform;

        List<XElement> SupportedExtensions()
        {
            return xSpecTree.Elements("extensions").Elements("extension").Where(e => e.Attribute("supported").Value != "disabled").ToList();
        }

        List<XElement> DisabledExtensions()
        {
            return xSpecTree.Elements("extensions").Elements("extension").Where(e => e.Attribute("supported").Value == "disabled" || disabledExtensions.Contains(e.Attribute("name").Value)).ToList();
        }

        HashSet<string> disabledExtensions = new HashSet<string>()
        {
            "VK_FUCHSIA_imagepipe_surface",
        };

        void GeneratePlatformExtensions()
        {
            GeneratePlatformExtensions("Android", platformExtensionsToGenerate["Android"]);
            GeneratePlatformExtensions("Linux", platformExtensionsToGenerate["Linux"]);
            GeneratePlatformExtensions("Windows", platformExtensionsToGenerate["Windows"]);
            GeneratePlatformExtensions("iOS", platformExtensionsToGenerate["iOS"]);
        }

        // Mapping from PlatformName to Array of ExtensionNames
        Dictionary<string, string[]> platformExtensionsToGenerate = new Dictionary<string, string[]>()
        {
            {
                "Android", new string[]
                {
                    "VK_KHR_android_surface",
                }
            },
            {
                "Linux", new string[]
                {
                    "VK_KHR_xlib_surface",
                    "VK_KHR_xcb_surface",
                    "VK_KHR_wayland_surface",
                    "VK_KHR_mir_surface"
                }
            },
            {
                "Windows", new string[]
                {
                    "VK_KHR_win32_surface",
                    "VK_NV_external_memory_win32",
                    "VK_NV_win32_keyed_mutex",
                }
            },
            {
                "iOS", new string[]
                {
                    "VK_MVK_ios_surface",
                }
            },
        };

        void GeneratePlatformExtensions(string name, string[] extensionNames)
        {
            platform = name;

            var origOutputDir = outputDir;
            outputDir = Path.Combine(outputDir, platform);

            platformExtensionsRequiredTypes = new HashSet<string>();
            platformExtensionsRequiredCommands = new HashSet<string>();

            foreach (var extensionName in extensionNames)
                PrepareExtensionSets(extensionName);

            LearnStructsAndUnions();
            GenerateFileStructsCs();
            GenerateFileImportedCommandsCs();
            GenerateFileHandlesCs();

            outputDir = origOutputDir;
        }

        HashSet<string> platformExtensionsRequiredTypes = null;
        HashSet<string> platformExtensionsRequiredCommands = null;

        void PrepareExtensionSets(string extensionName)
        {
            var elements = SupportedExtensions().Single(e => e.Attribute("name").Value == extensionName).Elements("require");

            foreach (var element in elements.Elements())
            {
                switch (element.Name.ToString())
                {
                    case "type":
                        platformExtensionsRequiredTypes.Add(element.Attribute("name").Value);
                        break;
                    case "command":
                        platformExtensionsRequiredCommands.Add(element.Attribute("name").Value);
                        break;
                }
            }
        }

        #endregion

        #region WriteFileTypesXml

        void WriteFileTypesXml()
        {
            var doc = new XmlDocument();

            XmlElement types = doc.CreateElement("types");
            doc.AppendChild(types);

            WriteTypes(doc, types, "enum", enumInfos.Values.ToArray());
            WriteTypes(doc, types, "structure", structures.Values.ToArray());

            doc.Save(outputDir + Path.DirectorySeparatorChar + "types.xml");
        }

        void WriteTypes(XmlDocument doc, XmlElement types, string elementName, MemberTypeInfo[] typeInfo)
        {
            foreach (var info in typeInfo)
            {
                var element = doc.CreateElement(elementName);
                element.SetAttribute("name", info.name);
                element.SetAttribute("csName", info.csName);
                types.AppendChild(element);

                if (info.members == null)
                    continue;

                foreach (var member in info.members)
                {
                    var memberElement = doc.CreateElement("member");
                    memberElement.SetAttribute("name", member.Value);
                    memberElement.SetAttribute("csName", member.Key);
                    element.AppendChild(memberElement);
                }
            }
        }

        #endregion
    }
}
