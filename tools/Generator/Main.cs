using System;
using System.IO;

namespace VulkanSharp.Generator
{
    public class MainClass
    {
        /// <summary>
        /// --update
        /// --indentWithSpaces
        /// --writeComments
        /// </summary>
        static public int Main(string[] args)
        {
            var update = false;
            var outputDir = "../../src";
            Writer.IndentWithSpaces = false;
            GeneratorBase.WriteComments = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--update")
                {
                    i++;
                    update = true;
                }
                if (args[i] == "--indentWithSpaces")
                {
                    i++;
                    Writer.IndentWithSpaces = true;
                }
                if (args[i] == "--writeComments")
                {
                    i++;
                    GeneratorBase.WriteComments = true;
                }
            }

            var version_master = "master";
            var version_master_1_1_87 = "911a7646949e661b24ad6111479029ed9e841284";
            var version_master_1_1_88 = "0a7a04f32bd473bc7428efdbbbe132f33afad68c";
            var version_master_1_1_89 = "8d6a7b23a7decb5161e0e4a3297c4665f9061b0e";
            var version_master_1_1_90 = "099174883dbbad1b812f1767182e22d91bfd35dd";
            var version_master_1_1_91 = "e24e42dcffe39c4f69719ea724958a9fe2d7b952";
            var version_master_1_1_95 = "ef29cea94bba541fe1ec9ffc33f65af1268bacad";

            var use_version = version_master;

            var vk_xml_url = $"https://raw.githubusercontent.com/KhronosGroup/Vulkan-Docs/{use_version}/xml/vk.xml";

            var vk_xml_local = Path.Combine(outputDir, Path.GetFileName(vk_xml_url));

            if (update)
            {
                new System.Net.WebClient().DownloadFile(vk_xml_url, vk_xml_local);
            }

            new Generator(vk_xml_local, outputDir).Run();

            Console.WriteLine("");
            Console.WriteLine("Generation finished.");
            Console.ReadLine();

            return 0;
        }
    }
}

