﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CSharp;
using System.CodeDom.Compiler;

namespace MinecraftClient.ChatBots
{
    /// <summary>
    /// Runs a list of commands
    /// </summary>

    public class Script : ChatBot
    {
        private string file;
        private string[] lines = new string[0];
        private string[] args = new string[0];
        private int sleepticks = 10;
        private int nextline = 0;
        private string owner;
        private bool csharp;
        private Thread thread;
        private ManualResetEvent tpause;

        public Script(string filename)
        {
            ParseArguments(filename);
        }

        public Script(string filename, string ownername)
            : this(filename)
        {
            if (ownername != "")
                owner = ownername;
        }

        private void ParseArguments(string argstr)
        {
            List<string> args = new List<string>();
            StringBuilder str = new StringBuilder();

            bool escape = false;
            bool quotes = false;

            foreach (char c in argstr)
            {
                if (escape)
                {
                    if (c != '"')
                        str.Append('\\');
                    str.Append(c);
                    escape = false;
                }
                else
                {
                    if (c == '\\')
                        escape = true;
                    else if (c == '"')
                        quotes = !quotes;
                    else if (c == ' ' && !quotes)
                    {
                        if (str.Length > 0)
                            args.Add(str.ToString());
                        str.Clear();
                    }
                    else str.Append(c);
                }
            }

            if (str.Length > 0)
                args.Add(str.ToString());

            if (args.Count > 0)
            {
                file = args[0];
                args.RemoveAt(0);
                this.args = args.ToArray();
            }
            else file = "";
        }

        public static bool LookForScript(ref string filename)
        {
            //Automatically look in subfolders and try to add ".txt" file extension
            char dir_slash = Program.isUsingMono ? '/' : '\\';
            string[] files = new string[]
            {
                filename,
                filename + ".txt",
                filename + ".cs",
                "scripts" + dir_slash + filename,
                "scripts" + dir_slash + filename + ".txt",
                "scripts" + dir_slash + filename + ".cs",
                "config" + dir_slash + filename,
                "config" + dir_slash + filename + ".txt",
                "config" + dir_slash + filename + ".cs",
            };

            foreach (string possible_file in files)
            {
                if (System.IO.File.Exists(possible_file))
                {
                    filename = possible_file;
                    return true;
                }
            }

            return false;
        }

        public override void Initialize()
        {
            //Load the given file from the startup parameters
            if (LookForScript(ref file))
            {
                lines = System.IO.File.ReadAllLines(file);
                csharp = file.EndsWith(".cs");
                thread = null;

                if (owner != null)
                    SendPrivateMessage(owner, "Script '" + file + "' loaded.");
            }
            else
            {
                LogToConsole("File not found: '" + file + "'");

                if (owner != null)
                    SendPrivateMessage(owner, "File not found: '" + file + "'");
                
                UnloadBot(); //No need to keep the bot active
            }
        }

        public override void Update()
        {
            if (csharp) //C# compiled script
            {
                //Initialize thread on first update
                if (thread == null)
                {
                    tpause = new ManualResetEvent(false);
                    thread = new Thread(() =>
                    {
                        if (!RunCSharpScript() && owner != null)
                            SendPrivateMessage(owner, "Script '" + file + "' failed to run.");
                    });
                    thread.Start();
                }

                //Let the thread run for a short span of time
                if (thread != null)
                {
                    tpause.Set();
                    tpause.Reset();
                    if (thread.Join(100))
                        UnloadBot();
                }
            }
            else //Classic MCC script interpreter
            {
                if (sleepticks > 0) { sleepticks--; }
                else
                {
                    if (nextline < lines.Length) //Is there an instruction left to interpret?
                    {
                        string instruction_line = lines[nextline].Trim(); // Removes all whitespaces at start and end of current line
                        nextline++; //Move the cursor so that the next time the following line will be interpreted

                        if (instruction_line.Length > 1)
                        {
                            if (instruction_line[0] != '#' && instruction_line[0] != '/' && instruction_line[1] != '/')
                            {
                                instruction_line = Settings.ExpandVars(instruction_line);
                                string instruction_name = instruction_line.Split(' ')[0];
                                switch (instruction_name.ToLower())
                                {
                                    case "wait":
                                        int ticks = 10;
                                        try
                                        {
                                            ticks = Convert.ToInt32(instruction_line.Substring(5, instruction_line.Length - 5));
                                        }
                                        catch { }
                                        sleepticks = ticks;
                                        break;
                                    default:
                                        if (!PerformInternalCommand(instruction_line))
                                        {
                                            Update(); //Unknown command : process next line immediately
                                        }
                                        else if (instruction_name.ToLower() != "log") { LogToConsole(instruction_line); }
                                        break;
                                }
                            }
                            else { Update(); } //Comment: process next line immediately
                        }
                    }
                    else
                    {
                        //No more instructions to interpret
                        UnloadBot();
                    }
                }
            }
        }

        private bool RunCSharpScript()
        {
            //Script compatibility check for handling future versions differently
            if (lines.Length < 1 || lines[0] != "//MCCScript 1.0")
            {
                LogToConsole("Script file '" + file + "' does not start with a valid //MCCScript identifier.");
                return false;
            }

            //Process different sections of the script file
            bool scriptMain = true;
            List<string> script = new List<string>();
            List<string> extensions = new List<string>();
            foreach (string line in lines)
            {
                if (line.StartsWith("//MCCScript"))
                {
                    if (line.EndsWith("Extensions"))
                        scriptMain = false;
                }
                else if (scriptMain)
                {
                    script.Add(line);
                    //Add breakpoints for step-by-step execution of the script
                    if (tpause != null && line.Trim().EndsWith(";"))
                        script.Add("tpause.WaitOne();");
                }
                else extensions.Add(line);
            }

            //Generate a ChatBot class, allowing access to the ChatBot API
            string code = String.Join("\n", new string[]
            {
                "using System;",
                "using System.IO;",
                "using System.Threading;",
                "using MinecraftClient;",
                "namespace ScriptLoader {",
                "public class Script : ChatBot {",
                "public void __run(ChatBot master, ManualResetEvent tpause, string[] args) {",
                "SetMaster(master);",
                    String.Join("\n", script),
                "}",
                    String.Join("\n", extensions),
                "}}",
            });

            //Compile the C# class in memory using all the currently loaded assemblies
            CSharpCodeProvider compiler = new CSharpCodeProvider();
            CompilerParameters parameters = new CompilerParameters();
            parameters.ReferencedAssemblies
                .AddRange(AppDomain.CurrentDomain
                        .GetAssemblies()
                        .Where(a => !a.IsDynamic)
                        .Select(a => a.Location).ToArray());
            parameters.CompilerOptions = "/t:library";
            parameters.GenerateInMemory = true;
            CompilerResults result
                = compiler.CompileAssemblyFromSource(parameters, code);

            //Process compile warnings and errors
            if (result.Errors.Count > 0)
            {
                LogToConsole("Error loading '" + file + "':\n" + result.Errors[0].ErrorText);
                return false;
            }

            //Run the compiled script with exception handling
            object compiledScript = result.CompiledAssembly.CreateInstance("ScriptLoader.Script");
            try { compiledScript.GetType().GetMethod("__run").Invoke(compiledScript, new object[] { this, tpause, args }); }
            catch (Exception e)
            {
                LogToConsole("Runtime error for '" + file + "':\n" + e);
                return false;
            }

            return true;
        }
    }
}
