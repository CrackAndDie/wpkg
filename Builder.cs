using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WindowsPackager.ARFileFormat;
using ICSharpCode.SharpZipLib.Tar;
using System.IO.Compression;
using System.Collections.Generic;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;

namespace Wpkg
{
	class Builder
	{
		private const LFileMode ArFileMode = LFileMode.S_IRUSR | LFileMode.S_IWUSR | LFileMode.S_IRGRP | LFileMode.S_IROTH | LFileMode.S_IFREG;
		private static string LOCAL_DIR = Environment.CurrentDirectory;
		private static string DebFileName;
		private const int EXIT_FILE_ERROR = 500;
		private const string ERRMSG_FILE_FAILURE = "E: Specified file does not exist! Aborting...";
		private const string ERRMSG_SPEC_FAILURE = "E: No spec file found! Aborting...";
		private const string ERRMSG_NAME_FAILURE = "E: Spec file contains no name! Aborting...";
		private const string ERRMSG_WSL_FAILURE = "E: No installed WSL distro with rpmbuild installed found! Aborting...";
		private const int EXIT_SPEC_ERROR = 600;
		private const int EXIT_NAME_ERROR = 700;
		private const int EXIT_WSL_ERROR = 800;

		public static void Dos2Unix(string[] files)
		{
			foreach (var file in files)
			{
				Console.WriteLine($"dos2unix {file}");

				var text = File.ReadAllText(file)
					.Replace("\r\n", "\n");
				File.WriteAllText(file, text);
			}
		}

		private static string WSLPath(string path) => Regex.Replace(Path.GetFullPath(path), "^(?<drive>[A-Z]):",
			match => $"/mnt/{match.Groups["drive"].Value.ToLower()}", RegexOptions.IgnoreCase | RegexOptions.Singleline)
			.Replace(Path.DirectorySeparatorChar, '/');
		public static void BuildRPMPackage(string pathToPackage, List<string> execs)
		{
			var distros = WSLShell.Default.InstalledDistros;
			var rpmCompatibleDistro = distros
				.Select(distro => new WSLShell(distro))
				.FirstOrDefault(wsl => wsl.Find("rpmbuild") != null);
			var shell = rpmCompatibleDistro;
			if (shell == null) Program.ExitWithMessage(ERRMSG_WSL_FAILURE, EXIT_WSL_ERROR);
			shell.Redirect = true;
			Console.WriteLine($"Found WSL distro with rpmbuild installed: {shell.CurrentDistroName}");
			Console.WriteLine();

			string WorkingDirectory = "";
			if (String.IsNullOrEmpty(pathToPackage))
			{
				WorkingDirectory = LOCAL_DIR;
			}
			else
			{
				WorkingDirectory = pathToPackage;
			}
			var specFile = Directory.EnumerateFiles(WorkingDirectory + "\\SPECS", "*.spec").FirstOrDefault();
			if (specFile == null) Program.ExitWithMessage(ERRMSG_SPEC_FAILURE, EXIT_SPEC_ERROR);
			var spec = File.ReadAllText(specFile);
			var packageName = Regex.Match(spec, "(?<=^Name:\\s*).*$", RegexOptions.Multiline)?.Value.Trim();
			if (packageName == null) Program.ExitWithMessage(ERRMSG_NAME_FAILURE, EXIT_NAME_ERROR);
			var packageVersion = Regex.Match(spec, "(?<=^Version:\\s*).*$", RegexOptions.Multiline)?.Value.Trim();

			BuildDataTarball(WorkingDirectory, execs, $"{packageName}-{packageVersion}/");

			var srcFile = $"{WorkingDirectory}\\{packageName}-{packageVersion}.tar.gz";
			using (var DataAsStream = CreateStream(WorkingDirectory, 1))
			using (var pkg = File.Create(srcFile))
			{
				DataAsStream.CopyTo(pkg);
			}

			File.Delete($"{WorkingDirectory}\\data.tar");
			File.Delete($"{WorkingDirectory}\\data.tar.gz");

			var homeSpecFile = specFile.Replace(WorkingDirectory, "~/rpmbuild").Replace(Path.DirectorySeparatorChar, '/');
			var homeSrcFile = srcFile.Replace(WorkingDirectory, "~/rpmbuild/SOURCES").Replace(Path.DirectorySeparatorChar, '/');

			if (shell.Find("rpmdev-setuptree") != null) shell.Exec("rpmdev-setuptree");
			else shell.Exec("mkdir -p ~/rpmbuild/SOURCES ~/rpmbuild/SPECS");

			shell.Exec($@"cp ""{WSLPath(specFile)}"" {homeSpecFile}");
			shell.Exec($@"cp ""{WSLPath(srcFile)}"" {homeSrcFile}");

			File.Delete(srcFile);

			if (shell.Find("rpmlint") != null) shell.Exec($"rpmlint {homeSpecFile}");
			shell.Exec($"rpmbuild -bb {homeSpecFile}");
			shell.Exec($@"cp -r ~/rpmbuild/RPMS/* ""{WSLPath($"{WorkingDirectory}\\RPMS")}""");

			var rpmsDir = WorkingDirectory + "\\RPMS";
			if (Directory.Exists(rpmsDir))
			{
				foreach (var rpmfile in Directory.EnumerateFiles(rpmsDir, "*.rpm", SearchOption.AllDirectories))
				{
					if (!Regex.IsMatch(Path.GetFileName(rpmfile), $"^{Regex.Escape(packageName)}-{Regex.Escape(packageVersion)}-")) File.Delete(rpmfile);
					else
					{
						var workrmpfile = Path.Combine(WorkingDirectory, Path.GetFileName(rpmfile));
						if (File.Exists(workrmpfile)) File.Delete(workrmpfile);
						File.Move(rpmfile, workrmpfile);
					}
				}
				Directory.Delete(rpmsDir, true);
			}
		}

		public static void BuildAppPackage(string pathToPackage, List<string> execs)
		{
			var origDir = pathToPackage;
			pathToPackage = Program.GetCaseSensitivePath(Path.GetFullPath(pathToPackage));
			var cwd = Environment.CurrentDirectory;
			Environment.CurrentDirectory = pathToPackage;

			string zipName = "outZip.zip";
			string zipFullPath = GetDirectoryFromFilePath(Environment.CurrentDirectory.TrimEnd('/', '\\')).TrimEnd('/', '\\') + "/" + zipName;
			Stream outStream = File.Create(zipFullPath);
			ZipOutputStream zipStream = new ZipOutputStream(outStream);

			Console.WriteLine($"Creating {zipFullPath}");

			var files = Directory.EnumerateFiles(pathToPackage, "*", SearchOption.AllDirectories);
			foreach (var file in files)
			{
				AddToZip(zipStream, file, execs);
			}

			zipStream.Finish();
			zipStream.Close();

			Environment.CurrentDirectory = cwd;

			Console.WriteLine();
		}

		public static void BuildDebPackage(string pathToPackage)
		{
			string WorkingDirectory = "";
			if (String.IsNullOrEmpty(pathToPackage))
			{
				WorkingDirectory = LOCAL_DIR;
			}
			else
			{
				WorkingDirectory = pathToPackage;
			}
			//Program.VerifyStructure(WorkingDirectory);

			Version DebianVersion = new Version(2, 0);
			Stream DebFileStream = new MemoryStream();

			Stream ControlAsStream = CreateStream(WorkingDirectory, 0);
			Stream DataAsStream = CreateStream(WorkingDirectory, 1);

			ARFileCreator.WriteMagic(DebFileStream);
			ARFileCreator.WriteEntry(DebFileStream, "debian-binary", ArFileMode, DebianVersion + "\n");
			ARFileCreator.WriteEntry(DebFileStream, "control.tar.gz", ArFileMode, ControlAsStream);
			ARFileCreator.WriteEntry(DebFileStream, "data.tar.gz", ArFileMode, DataAsStream);

			var fs = File.Create(WorkingDirectory + "\\" + DebFileName);
			DebFileStream.Seek(0, SeekOrigin.Begin);
			DebFileStream.CopyTo(fs);
			fs.Close();

			ControlAsStream.Close();
			DataAsStream.Close();

			File.Delete(WorkingDirectory + "\\control.tar");
			File.Delete(WorkingDirectory + "\\data.tar");
			File.Delete(WorkingDirectory + "\\control.tar.gz");
			File.Delete(WorkingDirectory + "\\data.tar.gz");
			File.Delete(WorkingDirectory + "\\debian-binary");

			Console.WriteLine($"Created {DebFileName}");
		}

		public static void AddToTar(TarArchive arch, FileSystemInfo info, List<string> execs, string prefix = "./")
		{
			TarEntry entry = TarEntry.CreateEntryFromFile(info.FullName);

			entry.Name = prefix + entry.Name;
			entry.UserId = 0;
			entry.UserName = "root";
			entry.GroupId = 0;
			entry.GroupName = "root";
			if (info is FileInfo) {
				if (entry.Name.Contains("/bin/") || info.FullName.EndsWith(".exe") ||
					info.Name == "preinst" || info.Name == "postinst" || info.Name == "prerm" || info.Name == "postrm" ||
					execs.Contains(entry.Name)) 
					entry.TarHeader.Mode = Convert.ToInt32("777", 8);
				else entry.TarHeader.Mode = Convert.ToInt32("666", 8);
			} else entry.TarHeader.Mode = Convert.ToInt32("777", 8);
			if (!Program.Options.SilentMode)
				Console.WriteLine($"  add {entry.Name}");
			arch.WriteEntry(entry, false);

			if (info is DirectoryInfo dir)
			{
				foreach (var subinfo in dir.EnumerateFileSystemInfos()) AddToTar(arch, subinfo, execs, prefix);
			}
		}

		// https://stackoverflow.com/questions/434641/how-do-i-set-permissions-attributes-on-a-file-in-a-zip-file-using-pythons-zip/6297838#6297838
		// https://unix.stackexchange.com/questions/14705/the-zip-formats-external-file-attribute/14727#14727
		// https://github.com/dotnet/runtime/issues/17912
		public static void AddToZip(ZipOutputStream arch, string info, List<string> execs)
		{
			// file name shite
			var currDirForEntry = GetDirectoryFromFilePath(Environment.CurrentDirectory.TrimEnd('/', '\\')); // skipping last part of a path
			string fileName = info.Replace('\\', '/').Replace(currDirForEntry.Replace('\\', '/'), "").TrimStart('/');

			ZipEntry entry = new ZipEntry(fileName);

			if (File.Exists(info))
			{
				if (entry.Name.Contains("/bin/") || execs.Contains(fileName))
					entry.ExternalFileAttributes = Convert.ToInt32("777", 8) << 16;
				else entry.ExternalFileAttributes = Convert.ToInt32("666", 8) << 16;
			}
			if (!Program.Options.SilentMode)
				Console.WriteLine($"  add {entry.Name}");

			arch.PutNextEntry(entry);
			var bts = File.ReadAllBytes(info);
			arch.Write(bts, 0, bts.Length);
		}

		public static string GetDirectoryFromFilePath(string filePath)
		{
			return string.Concat(filePath.Replace("\\", "/").Reverse().SkipWhile(x => x != '/').Reverse());
			// return Path.GetDirectoryName(filePath);
		}

		public static void BuildDataTarball(string directory, List<string> execs, string prefix = "./")
		{
			var origDir = directory;
			directory = Program.GetCaseSensitivePath(Path.GetFullPath(directory));
			var cwd = Environment.CurrentDirectory;
			Environment.CurrentDirectory = directory;

			//Console.WriteLine($"Tar {directory} to data.tar");
			string TarballName = "data.tar";
			Stream outStream = File.Create(directory + "\\" + TarballName);
			Stream tarballStream = new TarOutputStream(outStream, Encoding.ASCII);
			TarArchive dataTar = TarArchive.CreateOutputTarArchive(tarballStream);

			Console.WriteLine($"Creating {origDir + "\\" + TarballName}");

			// fix str (mandatory hotfix due to SharpZipLib)
			/* Console.WriteLine($"Current Directory: {Environment.CurrentDirectory}");
			dataTar.RootPath = Path.GetFullPath(directory).Replace('\\', '/');
			if (dataTar.RootPath.EndsWith("/"))
			{
				dataTar.RootPath = dataTar.RootPath.Remove(dataTar.RootPath.Length - 1);
			}*/

			//Console.WriteLine($"Tar RootPath: {dataTar.RootPath}");

			DirectoryInfo[] subdirs = new DirectoryInfo(directory).GetDirectories();
			foreach (var dir in subdirs)
			{
				if (dir.Name == "DEBIAN" || dir.Name == "SPECS")
				{
					continue;
				}
	
				AddToTar(dataTar, dir, execs, prefix);
			}

			dataTar.Close();

			Environment.CurrentDirectory = cwd;

			Console.WriteLine();
		}

		public static void BuildControlTarball(string directory, List<string> execs)
		{
			var origDir = directory;
			directory = Program.GetCaseSensitivePath(Path.GetFullPath(directory));

			var debdirectory = directory + "\\DEBIAN";

			var cwd = Environment.CurrentDirectory;
			Environment.CurrentDirectory = debdirectory;

			//Console.WriteLine($"Tar {directory} to control.tar");
			string TarballName = "control.tar";
			Stream outStream = File.Create(directory + "\\" + TarballName);
			Stream tarballStream = new TarOutputStream(outStream, Encoding.ASCII);
			TarArchive controlTar = TarArchive.CreateOutputTarArchive(tarballStream);

			Console.WriteLine($"Creating {origDir + "\\" + TarballName}");

			// fix str (mandatory hotfix due to SharpZipLib)
			/* controlTar.RootPath = new DirectoryInfo(directory+"\\DEBIAN").FullName.Replace('\\', '/');
			if (controlTar.RootPath.EndsWith("/"))
			{
				controlTar.RootPath = controlTar.RootPath.Remove(controlTar.RootPath.Length - 1);
			} */

			// generate filename
			var control = File.ReadAllText(debdirectory + "\\control");
			DebFileName = Regex.Match(control, "(?<=^Package:\\s*)[^\\s]+.*$", RegexOptions.Multiline).Value.Trim() + ".deb";

			// scan for eligible control.tar entries & add them
			var files = new DirectoryInfo(debdirectory).EnumerateFiles();
			foreach (var item in files)
			{
				var fn = item.Name;
				//if (fn.Equals("control") || fn.Equals("preinst") || fn.Equals("postinst") || fn.Equals("prerm") || fn.Equals("postrm"))
				//{
					// DEBUG: Console.WriteLine("Found match: " + fn);

					AddToTar(controlTar, item, execs: execs);
				//}
			}

			controlTar.Close();

			Environment.CurrentDirectory = cwd;
			Console.WriteLine();
		}

		public static Stream CreateStream(string FileLocation, int TypeOfStream)
		{
			string WorkingType = "";
			if (TypeOfStream == 0)
			{
				WorkingType = FileLocation + "\\control.tar";
			}
			else if (TypeOfStream == 1)
			{
				WorkingType = FileLocation + "\\data.tar";
			}
			else
			{
				WorkingType = FileLocation;
			}
			try
			{
				Stream fs = File.OpenRead(WorkingType);

				if (TypeOfStream == 0 || TypeOfStream == 1)
				{
					// gzip the tar
					using (fs)
					using (var gzip = new GZipStream(File.Create(WorkingType + ".gz"), CompressionLevel.Optimal, false)) 
						fs.CopyTo(gzip);

					fs = File.OpenRead(WorkingType + ".gz");
				}
				return fs;
			}
			catch (FileNotFoundException)
			{
				Program.ExitWithMessage(ERRMSG_FILE_FAILURE, EXIT_FILE_ERROR);
				return null;
			}
		}

		public static Stream CreateStream(string FileName)
		{
			try
			{
				Stream fs = File.OpenRead(LOCAL_DIR + "\\" + FileName);
				return fs;
			}
			catch (FileNotFoundException)
			{
				Program.ExitWithMessage(ERRMSG_FILE_FAILURE, EXIT_FILE_ERROR);
				return null;
			}
		}
	}
}
