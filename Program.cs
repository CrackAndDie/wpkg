using CommandLine;
using CommandLine.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wpkg
{
	public class WpkgOptions
	{
		[Option('b', "build", Default = null, HelpText = "Path to the folder to build a .deb file")]
		public string DebianPackage { get; set; }

		[Option('r', "rpm", Default = null, HelpText = "Path to the folder to build a .rpm file")]
		public string RpmPackage { get; set; }

		[Option('a', "app", Default = null, HelpText = "Path to the folder to build a .zip with an .app folder for MacOS")]
		public string AppPackage { get; set; }

		[Option("d2u", Default = null, HelpText = "Convert dos to unix. Specify files divided by ';' or ','")]
		public string Dos2Unix { get; set; }

		[Option('x', "extract", Default = new string[0], HelpText = "Extracts specified .deb file to the specified folder")]
		public string[] ExtractDeb { get; set; }

		[Option('t', "theme", Default = null, HelpText = "This will create an empty base structure for an .deb with the specified file name")]
		public string ThemeDeb { get; set; }

		[Option('e', "execs", Default = "./execs.txt", HelpText = "The file with a list of files that should be 'chmoded' as 777")]
		public string ExecsPath { get; set; }

		[Option('s', "silent", Default = false, HelpText = "Is the 'silent' mode should be enabled")]
		public bool SilentMode { get; set; }
	}

	public class Program
	{
		private static string LOCAL_DIR = Environment.CurrentDirectory;
		private static string[] ControlElements = {
				"Package: com.yourcompany.identifier",
				"Name: Name of the product",
				"Depends: ",
				"Architecture: any",
				"Description: This is a sample short description",
				"Maintainer: Maintainer Name",
				"Author: Author Name",
				"Section: Section",
				"Version: 1.0"
		  };
		private const string ERRMSG_DIR_FAILURE = "E: Directory was not found! Aborting...";
		private const string ERRMSG_FILE_FAILURE = "E: Specified file does not exist! Aborting...";
		private const string ERRMSG_ARGC_FAILURE = "E: Mismatch in arguments! (perhaps missing one or one too much?) Aborting...";
		private const string ERRMSG_DEB_FAILURE = "E: File is not a Debian Binary! Aborting...";
		private const string ERRMSG_STRUCT_FAILURE = "E: Directory does NOT match a standard structure! (Perhaps missing control?) Aborting...";
		private const int EXIT_ARGS_MISMATCH = 100;
		private const int EXIT_DIR_ERROR = 200;
		private const int EXIT_DEBFILE_ERROR = 300;
		private const int EXIT_STRUCT_ERROR = 400;

		public static WpkgOptions Options => _options;
		private static WpkgOptions _options;

		static void Main(string[] args)
		{
			var argsParser = Parser.Default;
			var taskToWait = argsParser.ParseArguments<WpkgOptions>(args).MapResult<WpkgOptions, Task>(RunWpkg, (_) =>
			{
				Console.WriteLine("Older help text (for history and there are more info about some commands): \n");
				InfoMessage();
				return Task.CompletedTask;
			});
			taskToWait.GetAwaiter().GetResult();
		}

		private static Task RunWpkg(WpkgOptions options)
		{
			_options = options;

			var cwd = Environment.CurrentDirectory;
			Environment.CurrentDirectory = Path.GetPathRoot(cwd);
			Environment.CurrentDirectory = GetCaseSensitivePath(cwd);

			// to chmod them
			List<string> execs = new List<string>();
			if (options.ExecsPath != null && File.Exists(options.ExecsPath))
			{
				var txt = File.ReadAllText(options.ExecsPath);
				execs.AddRange(txt.Split('\n').Select(x => x.TrimEnd('\r')));
			}

			if (options.DebianPackage != null)
			{
				if (Directory.Exists(options.DebianPackage))
				{
					BuilderDebType(options.DebianPackage, true, execs);
				}
				else
				{
					ExitWithMessage(ERRMSG_DIR_FAILURE, EXIT_DIR_ERROR);
				}
			}
			else if (options.AppPackage != null)
			{
				if (Directory.Exists(options.AppPackage))
				{
					BuilderAppType(options.AppPackage, true, execs);
				}
				else
				{
					ExitWithMessage(ERRMSG_DIR_FAILURE, EXIT_DIR_ERROR);
				}
			}
			else if (options.RpmPackage != null)
			{
				if (Directory.Exists(options.RpmPackage))
				{
					BuilderRPMType(options.RpmPackage, true, execs);
				}
				else
				{
					ExitWithMessage(ERRMSG_DIR_FAILURE, EXIT_DIR_ERROR);
				}
			}
			else if (options.ThemeDeb != null)
			{
				// create base theme dir
				string target = LOCAL_DIR + "\\Library\\Themes\\" + options.ThemeDeb + ".theme";
				Directory.CreateDirectory(target);
				// create the necessary subdirs
				Directory.CreateDirectory(target + "\\IconBundles");
				Directory.CreateDirectory(target + "\\Bundles\\com.apple.springboard");
				GenerateControlFile(LOCAL_DIR);
			}
			else if (options.ExtractDeb != null && options.ExtractDeb.Length > 0)
			{
				if (options.ExtractDeb.Length == 2)
				{
					// check if file exists & create extraction stream
					if (File.Exists(options.ExtractDeb[0]) && Directory.Exists(options.ExtractDeb[1]))
					{
						ExtractorType(options.ExtractDeb[0], null, options.ExtractDeb[1]);
					}
					else
					{
						ExitWithMessage(ERRMSG_ARGC_FAILURE, EXIT_ARGS_MISMATCH);
					}
				}
				else if (options.ExtractDeb.Length == 1)
				{
					// check if we have a path or direct filename => file cannot contain the '\' char
					if (options.ExtractDeb[0].Contains("\\"))
					{
						if (File.Exists(options.ExtractDeb[0]))
						{
							ExtractorType(options.ExtractDeb[0], null, Path.GetDirectoryName(options.ExtractDeb[0]));
						}
						else
						{
							ExitWithMessage(ERRMSG_ARGC_FAILURE, EXIT_ARGS_MISMATCH);
						}
					}
					else
					{
						if (File.Exists(LOCAL_DIR + "\\" + options.ExtractDeb[0]))
						{
							ExtractorType(LOCAL_DIR + "\\" + options.ExtractDeb[0], options.ExtractDeb[0], null);
						}
						else
						{
							ExitWithMessage(ERRMSG_FILE_FAILURE, EXIT_DEBFILE_ERROR);
						}
					}
				}
			}
			else if (options.Dos2Unix != null)
			{
				Builder.Dos2Unix(options.Dos2Unix.Split(';', ','));
			}
			else
			{
				InfoMessage();
			}

			return Task.CompletedTask;
		}

		public static string GetCaseSensitivePath(string path)
		{
			var root = Path.GetPathRoot(path);
			try
			{
				foreach (var name in path.Substring(root.Length).Split(Path.DirectorySeparatorChar))
					root = Directory.GetFileSystemEntries(root, name).First();
			}
			catch (Exception)
			{
				// Log("Path not found: " + path);
				root += path.Substring(root.Length);
			}
			return root;
		}

		private static void BuilderDebType(string WorkDir, bool IsSpecified, List<string> execs = null)
		{
			execs = execs ?? new List<string>();

			string dir = (IsSpecified) ? WorkDir : LOCAL_DIR;
			VerifyStructure(dir);
			Builder.BuildControlTarball(dir, execs);
			Builder.BuildDataTarball(dir, execs);
			Builder.BuildDebPackage(dir);
		}

		private static void BuilderRPMType(string WorkDir, bool IsSpecified, List<string> execs = null)
		{
			string dir = (IsSpecified) ? WorkDir : LOCAL_DIR;
			Builder.BuildRPMPackage(dir, execs);
		}

		private static void BuilderAppType(string WorkDir, bool IsSpecified, List<string> execs = null)
		{
			string dir = (IsSpecified) ? WorkDir : LOCAL_DIR;
			Builder.BuildAppPackage(dir, execs);
		}

		private static void ExtractorType(string PassedFilePath, string FileName, string TargetDirectory)
		{
			VerifyFile(PassedFilePath);
			var debName = Path.GetFileNameWithoutExtension(PassedFilePath);
			if (String.IsNullOrEmpty(TargetDirectory))
			{
				Stream DebFileStream = Builder.CreateStream(FileName);
				Extractor.ExtractEverything(debName, DebFileStream, LOCAL_DIR);
			}
			else
			{
				Stream DebFileStream = Builder.CreateStream(PassedFilePath, 3);
				Extractor.ExtractEverything(debName, DebFileStream, TargetDirectory);
			}
		}

		private static void VerifyFile(string PathToFile)
		{
			if (Extractor.IsDebianBinary(PathToFile) == false)
			{
				ExitWithMessage(ERRMSG_DEB_FAILURE, EXIT_DEBFILE_ERROR);
			}
		}

		public static void VerifyStructure(string directory)
		{
			int passed = 0;
			// check if we AT LEAST have 1 dir
			DirectoryInfo[] subdirs = new DirectoryInfo(directory).GetDirectories();
			if (subdirs.Length > 0)
			{
				passed++;
			}
			// check if we have a control file
			if (File.Exists(directory + "\\DEBIAN\\control"))
			{
				passed++;
			}
			// check if our struct matches
			if (passed != 2)
			{
				ExitWithMessage(ERRMSG_STRUCT_FAILURE, EXIT_STRUCT_ERROR);
			}
		}

		private static void GenerateControlFile(string WorkingDir)
		{
			File.WriteAllLines(WorkingDir + "\\control", ControlElements, Encoding.ASCII);
		}

		public static void ExitWithMessage(string Message, int ExitCode)
		{
			Console.WriteLine(Message);
			Environment.Exit(ExitCode);
		}

		private static void InfoMessage()
		{
			Console.WriteLine("Windows Packager (wpkg) v2.0 Guide");
			ColorizedMessage("Building:\n" +
				 "wpkg -b            - Build .deb inside the local directory\n" +
				 "wpkg -b <Path>     - Build .deb in the given path\n" +
				 "wpkg -r            - Build .rpm inside the local directory\n" +
				 "wpkg -r <Path>     - Build .rpm in the given path." +
				 "  For rpm creation:" +
				 "  The .spec file must reside in the SPECS folder. For this to\n" +
				 "	 work, you need to have an WSL distro with rpmbuild installed.\n",
				 ConsoleColor.DarkCyan);
			ColorizedMessage("Extraction:\n" +
				 "wpkg -x <PathToDeb> <DestFolder>   - Extract .deb to given path\n" +
				 "wpkg -x <PathToDeb>                - Extract .deb inside the original folder\n" +
				 "wpkg -x <DebfileName>              - Extract a .deb inside the folder you're in*\n" +
				 " *: only works if you're in the same folder as the .deb!\n",
				 ConsoleColor.DarkGreen);
			ColorizedMessage("Extras:\n" +
				 "wpkg -h                    - Show this helptext\n" +
				 "wpkg --d2u file1;file2;...  - Convert files from DOS to Unix\n" +
				 "wpkg --theme               - Create a base for an iOS Theme\n" +
				 "  in the directory you are currently\n",
				 ConsoleColor.DarkMagenta);
			ColorizedMessage("If you stumble upon an error, please send an email at\n" +
				 "support@saadat.dev\n",
				 ConsoleColor.DarkRed);
		}

		private static void ColorizedMessage(string Message, ConsoleColor cColor)
		{
			Console.ForegroundColor = cColor;
			Console.WriteLine(Message);
			Console.ForegroundColor = ConsoleColor.White;
		}
	}
}
