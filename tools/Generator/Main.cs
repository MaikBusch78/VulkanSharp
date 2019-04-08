using System;
using System.IO;

namespace VulkanSharp.Generator
{
    public class MainClass
    {
        /// <summary>
        /// --update [true|false]
        ///     default: false
        /// --outputDir path
        ///     default: ../../src
        /// --indentationKind [VisualStudio|Tab]
        ///     default: Tab
        /// --writeComments [true|false]
        ///     default: false
        /// </summary>
        static public int Main(string[] args)
        {
            var update = false;
            var outputDir = "../../src";
            Writer.IndentationKindVisualStudio = false;
            GeneratorBase.WriteComments = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--update")
                {
                    i++;
                    update = bool.Parse(args[i++]);
                }
                if (args[i] == "--outputDir")
                {
                    i++;
                    outputDir = args[i++];
                }
                if (args[i] == "--indentationKind")
                {
                    i++;
                    Writer.IndentationKindVisualStudio = args[i++] == "VisualStudio";
                }
                if (args[i] == "--writeComments")
                {
                    i++;
                    GeneratorBase.WriteComments = bool.Parse(args[i++]);
                }
            }

            var URL = "https://raw.githubusercontent.com/KhronosGroup/Vulkan-Docs/master/xml/vk.xml";
            var vkXml = Path.Combine(outputDir, Path.GetFileName(URL));

            if (update)
            {
                new System.Net.WebClient().DownloadFile(URL, vkXml);
            }

            new Generator(vkXml, outputDir).Run();

            return 0;
        }
    }
}

