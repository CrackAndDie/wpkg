﻿using System;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace Wpkg
{
	public class Shell : INotifyCompletion
	{
		const bool DoNotWaitForProcessExit = false;

		static int N = 0;

		public int ShellId = N++;
		public Shell() : base()
		{
			output = new StringBuilder();
			error = new StringBuilder();
			outputAndError = new StringBuilder();
			Log += OnLog;
			LogCommand += OnLogCommand;
			LogCommandEnd += OnLogCommandEnd;
			LogError += OnLogError;
			LogOutput += OnLogOutput;
		}

		//methods to support await on Shell type
		public Shell GetAwaiter() => this;

		Action Continuation = null;
		public void OnCompleted(Action continuation)
		{
			lock (this) Continuation += continuation;
			CheckCompleted();
		}

		bool errorEOF = true, outputEOF = true, hasProcessExited = true;
		int exitCode = 0;
		bool checkHasExited = true;

		// Process.HasExited can cause deadlock because it raises Exit event, therefore disable it in CheckCompleted by checkHasExited = false
		public bool IsCompleted => Process == null ||
			((DoNotWaitForProcessExit || hasProcessExited || (checkHasExited && Process.HasExited)) && errorEOF && outputEOF);
		public Shell GetResult() => this;
		public Shell Parent { get; set; } = null;
		public virtual char PathSeparator => Path.PathSeparator;
		public bool CreateNoWindow = true;
		public ProcessWindowStyle WindowStyle = ProcessWindowStyle.Normal;
		public virtual string ShellExe { get; } = IsWindows ? "Cmd" : "bash";

		Process process;
		public virtual Process Process
		{
			get { return process; }
			protected set
			{
				if (process != value)
				{
					lock (this)
					{
						hasProcessExited = outputEOF = errorEOF = value == null;
						process = value;
					}
				}
			}
		}

		public bool NotFound { get; set; }
		public virtual string Find(string cmd)
		{
			string file = null;
			if (cmd.IndexOf(Path.DirectorySeparatorChar) >= 0)
			{
				if (File.Exists(cmd)) file = cmd;
			}
			else
			{
				file = Environment.GetEnvironmentVariable("PATH")
					  .Split(new char[] { PathSeparator })
					  .SelectMany(p =>
					  {
						  var p1 = Path.Combine(p, cmd);
						  return new string[] { p1, Path.ChangeExtension(p1, "exe") };
					  })
					  .FirstOrDefault(p => File.Exists(p));
			}
			NotFound = file == null;
			return file;
		}

		protected virtual string ToTempFile(string script)
		{
			var file = Path.GetTempFileName();
			File.WriteAllText(file, script);
			return file;
		}

		void CheckCompleted()
		{
			Action cnt = null;
			var exited = Process.HasExited;
			lock (this)
			{
				hasProcessExited = hasProcessExited || exited;
				checkHasExited = false;
				if (IsCompleted && Continuation != null)
				{
					cnt = Continuation;
					Continuation = null;
				}
				checkHasExited = true;
			}
			cnt?.Invoke();
		}

		public virtual Shell ExecAsync(string cmd, Encoding encoding = null)
		{
			LogCommand?.Invoke(cmd);

			// separate command from arguments
			string arguments;
			if (cmd.Length > 0 && cmd[0] == '"') // command is a " delimited string
			{
				var pos = cmd.IndexOf('"', 1);
				if (pos >= 1)
				{
					if (pos < cmd.Length - 1)
					{
						arguments = cmd.Substring(pos + 1).Trim();
						cmd = cmd.Substring(1, pos - 1);
					}
					else
					{
						cmd = cmd.Substring(1, pos - 1);
						arguments = "";
					}
				}
				else
				{
					cmd = cmd.Substring(1);
					arguments = "";
				}
			}
			else // command is the first token of space separated tokens
			{
				var pos = cmd.IndexOf(' ');
				if (pos >= 0 && pos < cmd.Length - 1)
				{
					arguments = cmd.Substring(pos + 1);
					cmd = cmd.Substring(0, pos);
				}
				else arguments = "";
			}

			var cmdWithPath = Find(cmd);
			if (cmdWithPath != null)
			{
				var child = Clone;
				var process = new Process();
				child.Process = process;
				process.StartInfo.FileName = cmdWithPath;
				process.StartInfo.Arguments = arguments;
				process.StartInfo.UseShellExecute = false;
				process.StartInfo.CreateNoWindow = CreateNoWindow;
				process.StartInfo.WindowStyle = WindowStyle;
				process.StartInfo.RedirectStandardOutput = true;
				process.StartInfo.RedirectStandardError = true;
				process.StartInfo.StandardOutputEncoding = encoding ?? Encoding.Default;
				process.StartInfo.StandardErrorEncoding = encoding ?? Encoding.Default;
				process.Exited += (obj, args) =>
				{
					child.exitCode = child.Process.ExitCode;
					lock (child) child.hasProcessExited = true;
					child.CheckCompleted();
				};
				process.EnableRaisingEvents = true;
				process.ErrorDataReceived += (p, data) =>
				{
					if (data.Data == null)
					{
						lock (child) child.errorEOF = true;
						child.CheckCompleted();
					}
					else
					{
						var line = $"{data.Data}{Environment.NewLine}";
						var shell = child;
						while (shell != null)
						{
							shell.Log?.Invoke(line);
							shell.LogError?.Invoke(line);
							shell = shell.Parent;
						}
					}
				};
				process.OutputDataReceived += (p, data) =>
				{
					if (data.Data == null)
					{
						lock (child) child.outputEOF = true;
						child.CheckCompleted();
						LogCommandEnd?.Invoke();
					}
					else
					{
						var line = $"{data.Data}{Environment.NewLine}";
						var shell = child;
						while (shell != null)
						{
							shell.Log?.Invoke(line);
							shell.LogOutput?.Invoke(line);
							shell = shell.Parent;
						}
					}
				};
				process.Start();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				return child;
			}
			else
			{
				LogError?.Invoke($"Error {cmd} not found.{Environment.NewLine}");
				var child = Clone;
				child.Process = null;
				child.NotFound = true;
				return child;
			}
		}
		public virtual Shell Exec(string command, Encoding encoding = null) => ExecAsync(command, encoding).Task().Result;
		public virtual Shell Clone
		{
			get
			{
				Shell clone = Activator.CreateInstance(GetType()) as Shell;
				clone.Parent = this;
				return clone;
			}
		}

		public virtual Shell SilentClone
		{
			get
			{
				var clone = Clone;
				clone.Log = clone.LogCommand = clone.LogOutput = clone.LogError = null;
				clone.Parent = null;
				return clone;
			}
		}

		public virtual Shell ExecScriptAsync(string script, Encoding encoding = null)
		{
			script = script.Trim();
			// adjust new lines to OS type
			script = Regex.Replace(script, @"\r?\n", Environment.NewLine);
			var file = ToTempFile(script.Trim());
			var shell = ExecAsync($"{ShellExe} \"{file}\"", encoding);
			if (shell.Process != null)
			{
				shell.Process.Exited += (sender, args) =>
				{
					File.Delete(file);
				};
			}
			return shell;
		}

		public virtual Shell ExecScript(string script, Encoding encoding = null) => ExecScriptAsync(script, encoding).Task().Result;


		/* public virtual async Task<Shell> Wait(int milliseconds = Timeout.Infinite)
		{
			if (milliseconds == Timeout.Infinite) Process.WaitForExit();
			else Process.WaitForExit(milliseconds);
			return await this;
		} */

		public Action<string> Log { get; set; }
		public Action<string> LogCommand { get; set; }
		public Action LogCommandEnd { get; set; }
		public Action<string> LogOutput { get; set; }
		public Action<string> LogError { get; set; }

		StringBuilder output, error, outputAndError;

		public async Task<Shell> Task()
		{
			return await this;
		}

		public async Task<string> Output()
		{
			if (Process == null && NotFound) return null;
			await this;
			lock (output) return output.ToString();
		}

		public async Task<string> Error()
		{
			if (Process == null && NotFound) return null;
			await this;
			lock (error) return error.ToString();
		}
		public async Task<string> OutputAndError()
		{
			if (Process == null && NotFound) return null;
			await this;
			lock (outputAndError) return outputAndError.ToString();
		}

		public async Task<int> ExitCode()
		{
			if (Process == null && NotFound) return -500;
			await this;
			return exitCode;
		}
		public bool Redirect = false;
		public string LogFile = null;
		protected virtual void OnLog(string text)
		{
			lock (outputAndError)
			{
				outputAndError.Append(text);
				if (LogFile != null) File.AppendAllText(LogFile, text);
			}
		}

		protected virtual void OnLogCommand(string text)
		{
			text = $"> {text}";
			if (Redirect) Console.WriteLine(text);
			if (LogFile != null) File.AppendAllText(LogFile, text);
		}
		protected virtual void OnLogCommandEnd()
		{
			if (Redirect) Console.WriteLine();
			if (LogFile != null) File.AppendAllText(LogFile, Environment.NewLine);
		}
		protected virtual void OnLogOutput(string text)
		{
			lock (output)
			{
				output.Append(text);
				if (Redirect) Console.Write(text);
			}
		}
		protected virtual void OnLogError(string text)
		{
			lock (error) error.Append(text);
			if (Redirect) Console.Error.Write(text);
		}


#if wpkg
		public static bool IsWindows => RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
#else
		public static bool IsWindows => OSInfo.IsWindows;
#endif

#if wpkg
		public readonly static Shell Default = new Shell(); // OSInfo.Current.DefaultShell;
#else
		public static Shell Default => OSInfo.Current.DefaultShell;
#endif
	}
}