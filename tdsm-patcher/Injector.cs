﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace tdsm.patcher
{
    public partial class Injector : IDisposable
    {
        private AssemblyDefinition _asm;
        private AssemblyDefinition _self;

        public AssemblyDefinition TerrariaAssembly
        {
            get
            { return _asm; }
        }

        public AssemblyDefinition APIAssembly
        {
            get
            { return _self; }
        }

        public Injector(string filePath, string patchFile)
        {
            Initalise(filePath, patchFile);
        }

        private void Initalise(string filePath, string patchFile)
        {
            //Load the Terraria assembly
            using (var ms = new MemoryStream())
            {
                using (var fs = File.OpenRead(filePath))
                {
                    var buff = new byte[256];
                    while (fs.Position < fs.Length)
                    {
                        var task = fs.Read(buff, 0, buff.Length);
                        ms.Write(buff, 0, task);
                    }
                }

                ms.Seek(0L, SeekOrigin.Begin);
                _asm = AssemblyDefinition.ReadAssembly(ms);
            }
            //Load the assembly to patch to
            using (var ms = new MemoryStream())
            {
                using (var fs = File.OpenRead(patchFile))
                {
                    var buff = new byte[256];
                    while (fs.Position < fs.Length)
                    {
                        var task = fs.Read(buff, 0, buff.Length);
                        ms.Write(buff, 0, task);
                    }
                    fs.Close();
                }

                ms.Seek(0L, SeekOrigin.Begin);
                _self = AssemblyDefinition.ReadAssembly(ms);
            }

            InitOrganisers();
        }

        /// <summary>
        /// Checks to see if the source (Terraria) binary is a supported version
        /// </summary>
        /// <param name="server"></param>
        /// <returns></returns>
        public string GetAssemblyVersion()
        {
            return _asm.CustomAttributes
                .Single(x => x.AttributeType.Name == "AssemblyFileVersionAttribute")
                .ConstructorArguments
                .First()
                .Value as string;
        }

        public void HookDedServEnd()
        {
            var method = Terraria.Main.Methods.Single(x => x.Name == "DedServ");
            var replacement = API.MainCallback.Methods.Single(x => x.Name == "OnProgramFinished" && x.IsStatic);

            var imported = _asm.MainModule.Import(replacement);
            var il = method.Body.GetILProcessor();

            il.InsertBefore(method.Body.Instructions.Last(), il.Create(OpCodes.Call, imported));
        }

        public void HookConfig()
        {
            var main = Terraria.ProgramServer.Methods.Single(x => x.Name == "Main" && x.IsStatic);
            var replacement = API.Configuration.Methods.Single(x => x.Name == "Load" && x.IsStatic);

            //Grab all occurances of "LoadDedConfig" and route it to ours
            var toBeReplaced = main.Body.Instructions
                .Where(x => x.OpCode == Mono.Cecil.Cil.OpCodes.Callvirt
                    && x.Operand is MethodReference
                    && (x.Operand as MethodReference).Name == "LoadDedConfig"
                )
                .ToArray();

            for (var x = 0; x < toBeReplaced.Length; x++)
            {
                toBeReplaced[x].OpCode = OpCodes.Call;
                toBeReplaced[x].Operand = _asm.MainModule.Import(replacement);
            }
            var il = main.Body.GetILProcessor();
            for (var x = toBeReplaced.Length - 1; x > -1; x--)
            {
                il.Remove(toBeReplaced[x].Previous.Previous.Previous.Previous);
            }
        }

        public void HookInvasions()
        {
            var main = Terraria.NPC.Methods.Single(x => x.Name == "SpawnNPC" && x.IsStatic);

            var il = main.Body.GetILProcessor();
            var callback = API.NPCCallback.Methods.Single(x => x.Name == "OnInvasionNPCSpawn");

            var ins = main.Body.Instructions.Where(x =>
                x.OpCode == OpCodes.Ldsfld
                && x.Operand is FieldReference
                && (x.Operand as FieldReference).Name == "invasionType").ToArray()[1];

            il.InsertBefore(ins, il.Create(OpCodes.Ldloc_2));
            il.InsertBefore(ins, il.Create(OpCodes.Ldc_I4, 16));
            il.InsertBefore(ins, il.Create(OpCodes.Mul));
            il.InsertBefore(ins, il.Create(OpCodes.Ldc_I4_8));
            il.InsertBefore(ins, il.Create(OpCodes.Add));
            il.InsertBefore(ins, il.Create(OpCodes.Ldloc_3));
            il.InsertBefore(ins, il.Create(OpCodes.Ldc_I4, 16));
            il.InsertBefore(ins, il.Create(OpCodes.Mul));
            il.InsertBefore(ins, il.Create(OpCodes.Call, _asm.MainModule.Import(callback)));
        }

        public void FixStatusTexts()
        {
            var main = Terraria.WorldFile.Methods.Single(x => x.Name == "saveWorld" && x.IsStatic);

            var il = main.Body.GetILProcessor();
            var statusText = Terraria.Main.Fields.Single(x => x.Name == "statusText");

            var ins = main.Body.Instructions.Where(x => x.OpCode == OpCodes.Leave_S).Last();

            il.InsertBefore(ins, il.Create(OpCodes.Ldstr, ""));
            il.InsertBefore(ins, il.Create(OpCodes.Stsfld, statusText));
        }

        public void HookWorldFile_DEBUG()
        {
            var mth = Terraria.WorldGen.Methods.Single(x => x.Name == "serverLoadWorldCallBack" && x.IsStatic);
            var replacement = API.WorldFileCallback.Methods.Single(x => x.Name == "loadWorld" && x.IsStatic);

            var toBeReplaced = mth.Body.Instructions
                .Where(x => x.OpCode == Mono.Cecil.Cil.OpCodes.Call
                    && x.Operand is MethodReference
                    && (x.Operand as MethodReference).Name == "loadWorld"
                )
                .ToArray();

            for (var x = 0; x < toBeReplaced.Length; x++)
            {
                toBeReplaced[x].Operand = _asm.MainModule.Import(replacement);
            }

            //Make public
            var fld = Terraria.WorldGen.Fields.Single(x => x.Name == "lastMaxTilesX");
            fld.IsPrivate = false;
            fld.IsFamily = false;
            fld.IsPublic = true;

            fld = Terraria.WorldGen.Fields.Single(x => x.Name == "lastMaxTilesY");
            fld.IsPrivate = false;
            fld.IsFamily = false;
            fld.IsPublic = true;
        }

        public void HookStatusText()
        {
            var dedServ = Terraria.Main.Methods.Single(x => x.Name == "DedServ");
            var callback = API.MainCallback.Methods.Single(x => x.Name == "OnStatusTextChange");

            var startInstructions = dedServ.Body.Instructions
                .Where(x => x.OpCode == OpCodes.Ldsfld && x.Operand is FieldReference && (x.Operand as FieldReference).Name == "oldStatusText")
                .Reverse() //Remove desc
                .ToArray();

            var il = dedServ.Body.GetILProcessor();
            foreach (var ins in startInstructions)
            {
                var end = ins.Operand as Instruction;
                var ix = il.Body.Instructions.IndexOf(ins);

                var inLoop = il.Body.Instructions[ix].Previous.OpCode == OpCodes.Br_S;

                while (!(il.Body.Instructions[ix].OpCode == OpCodes.Call && il.Body.Instructions[ix].Operand is MethodReference && ((MethodReference)il.Body.Instructions[ix].Operand).Name == "WriteLine"))
                {
                    il.Remove(il.Body.Instructions[ix]);
                }
                il.Remove(il.Body.Instructions[ix]); //Remove the Console.WriteLine

                var insCallback = il.Create(OpCodes.Call, _asm.MainModule.Import(callback));
                il.InsertBefore(il.Body.Instructions[ix], insCallback);

                //Fix the loop back to the start
                if (inLoop && il.Body.Instructions[ix + 2].OpCode == OpCodes.Brfalse_S)
                {
                    il.Body.Instructions[ix + 2].Operand = insCallback;
                }
            }
        }

        public void HookNetMessage()
        {
            var method = Terraria.NetMessage.Methods.Single(x => x.Name == "SendData");
            var callback = API.NetMessageCallback.Methods.First(m => m.Name == "SendData");

            var il = method.Body.GetILProcessor();

            var ret = il.Create(OpCodes.Ret);
            var call = il.Create(OpCodes.Call, _asm.MainModule.Import(callback));
            var first = method.Body.Instructions.First();

            il.InsertBefore(first, il.Create(OpCodes.Nop));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_2));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_3));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_S, method.Parameters[4]));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_S, method.Parameters[5]));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_S, method.Parameters[6]));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_S, method.Parameters[7]));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_S, method.Parameters[8]));
            il.InsertBefore(first, call);
            il.InsertBefore(first, il.Create(OpCodes.Brtrue_S, first));
            il.InsertBefore(first, ret);
        }

        public void HookConsoleTitle()
        {
            var method = Terraria.Main.Methods.Single(x => x.Name == "DedServ");
            var callback = API.GameWindow.Methods.First(m => m.Name == "SetTitle");

            var il = method.Body.GetILProcessor();

            var replacement = _asm.MainModule.Import(callback);
            foreach (var ins in method.Body.Instructions
                .Where(x => x.OpCode == OpCodes.Call
                    && x.Operand is MethodReference
                    && (x.Operand as MethodReference).DeclaringType.FullName == "System.Console"
                    && (x.Operand as MethodReference).Name == "set_Title"))
            {
                ins.Operand = replacement;
            }
        }

        public void HookProgramStart()
        {
            var method = Terraria.ProgramServer.Methods.Single(x => x.Name == "Main");
            var callback = API.MainCallback.Methods.First(m => m.Name == "OnProgramStarted");

            var il = method.Body.GetILProcessor();

            var ret = il.Create(OpCodes.Ret);
            var call = il.Create(OpCodes.Call, _asm.MainModule.Import(callback));
            var first = method.Body.Instructions.First();

            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, call);
            il.InsertBefore(first, il.Create(OpCodes.Brtrue_S, first));
            il.InsertBefore(first, ret);
        }

        public void HookUpdateServer()
        {
            var method = Terraria.Main.Methods.Single(x => x.Name == "UpdateServer");
            var callback = API.MainCallback.Methods.First(m => m.Name == "UpdateServerEnd");

            var il = method.Body.GetILProcessor();
            il.InsertBefore(method.Body.Instructions.Last(), il.Create(OpCodes.Call, _asm.MainModule.Import(callback)));
        }

        public void HookInitialise()
        {
            var method = Terraria.Netplay.Methods.Single(x => x.Name == "Init");
            var callback = API.MainCallback.Methods.First(m => m.Name == "Initialise");

            var il = method.Body.GetILProcessor();
            var first = method.Body.Instructions.First();

            il.InsertBefore(first, il.Create(OpCodes.Call, _asm.MainModule.Import(callback)));

            il.InsertBefore(first, il.Create(OpCodes.Brtrue_S, first));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
        }

        public void HookWorldEvents()
        {
            var method = Terraria.WorldGen.Methods.Single(x => x.Name == "generateWorld");
            var callbackBegin = API.MainCallback.Methods.First(m => m.Name == "WorldGenerateBegin");
            var callbackEnd = API.MainCallback.Methods.First(m => m.Name == "WorldGenerateEnd");

            var il = method.Body.GetILProcessor();
            il.InsertBefore(method.Body.Instructions.First(), il.Create(OpCodes.Call, _asm.MainModule.Import(callbackBegin)));
            il.InsertBefore(method.Body.Instructions.Last(), il.Create(OpCodes.Call, _asm.MainModule.Import(callbackEnd)));

            method = Terraria.WorldFile.Methods.Single(x => x.Name == "loadWorld");

            callbackBegin = API.MainCallback.Methods.First(m => m.Name == "WorldLoadBegin");
            callbackEnd = API.MainCallback.Methods.First(m => m.Name == "WorldLoadEnd");

            il = method.Body.GetILProcessor();
            il.InsertBefore(method.Body.Instructions.First(), il.Create(OpCodes.Call, _asm.MainModule.Import(callbackBegin)));

            var old = method.Body.Instructions.Last();
            var newI = il.Create(OpCodes.Call, _asm.MainModule.Import(callbackEnd));

            for (var x = 0; x < method.Body.Instructions.Count; x++)
            {
                var ins = method.Body.Instructions[x];
                if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference)
                {
                    var mref = ins.Operand as MethodReference;
                    if (mref.Name == "setFireFlyChance")
                    {
                        il.InsertAfter(ins, newI);
                        break;
                    }
                }
            }
            //TODO work out why it crashes when you replace Ret with Ret
        }

        public void PatchServer()
        {
            var method = Terraria.Netplay.Methods.Single(x => x.Name == "StartServer");
            var callback = API.NetplayCallback.Methods.First(m => m.Name == "StartServer");

            var ins = method.Body.Instructions.Single(x => x.OpCode == OpCodes.Ldftn);
            ins.Operand = _asm.MainModule.Import(callback);

            //Make the Player inherit our defaults
            //var baseType = _self.MainModule.Types.Single(x => x.Name == "BasePlayer");
            //var interfaceType = _self.MainModule.Types.Single(x => x.Name == "ISender");

            Terraria.Player.BaseType = _asm.MainModule.Import(API.BasePlayer);

            //Make the UpdateServer function public
            var us = Terraria.Main.Methods.Single(x => x.Name == "UpdateServer");
            us.IsPrivate = false;
            us.IsPublic = true;

            ////Map ServerSock.CheckSection to our own
            //var repl = _asm.MainModule.Types
            //    .SelectMany(x => x.Methods)
            //    .Where(x => x.HasBody)
            //    .SelectMany(x => x.Body.Instructions)
            //    .Where(x => x.OpCode == OpCodes.Call && x.Operand is MethodReference && (x.Operand as MethodReference).Name == "CheckSection")
            //    .ToArray();
            //callback = userInputClass.Methods.First(m => m.Name == "CheckSection");
            //var mref = _asm.MainModule.Import(callback);
            //foreach (var inst in repl)
            //{
            //    inst.Operand = mref;
            //}
        }

        public void FixNetplay()
        {
            const String NATGuid = "AE1E00AA-3FD5-403C-8A27-2BBDC30CD0E1";
            var staticConstructor = Terraria.Netplay.Methods.Single(x => x.Name == ".cctor");

            var il = staticConstructor.Body.GetILProcessor();
            var counting = 0;
            for (var x = 0; x < staticConstructor.Body.Instructions.Count; x++)
            {
                var ins = staticConstructor.Body.Instructions[x];
                if (ins.OpCode == OpCodes.Ldstr && ins.Operand is String && ins.Operand as String == NATGuid)
                {
                    counting = 9;
                }

                if (counting-- > 0)
                {
                    il.Remove(ins);
                    x--;
                }
            }

            var fl = Terraria.Netplay.Fields.SingleOrDefault(x => x.Name == "upnpnat");
            if (fl != null)
                Terraria.Netplay.Fields.Remove(fl);

            //Clear open and close methods, add reference to the APIs
            var cb = Terraria.Netplay.Methods.Single(x => x.Name == "openPort");
            //    .Body;
            //cb.InitLocals = false;
            //cb.Variables.Clear();
            //cb.Instructions.Clear();
            Terraria.Netplay.Methods.Remove(cb);
            //cb.Instructions.Add(cb.GetILProcessor().Create(OpCodes.Nop));
            //cb.Instructions.Add(cb.GetILProcessor().Create(OpCodes.Ret));

            var close = Terraria.Netplay.Methods.Single(x => x.Name == "closePort");
            //    .Body;
            //close.InitLocals = false;
            //close.Variables.Clear();
            //close.Instructions.Clear();
            //close.Instructions.Add(cb.GetILProcessor().Create(OpCodes.Nop));
            //close.Instructions.Add(cb.GetILProcessor().Create(OpCodes.Ret));
            Terraria.Netplay.Methods.Remove(close);

            fl = Terraria.Netplay.Fields.SingleOrDefault(x => x.Name == "mappings");
            if (fl != null)
                Terraria.Netplay.Fields.Remove(fl);

            //use our uPNP (when using native terraria server)
            var openCallback = API.NAT.Methods.First(m => m.Name == "OpenPort");
            var closeCallback = API.NAT.Methods.First(m => m.Name == "ClosePort");

            var serverLoop = Terraria.Netplay.Methods.Single(x => x.Name == "ServerLoop");

            foreach (var ins in serverLoop.Body.Instructions
                .Where(x => x.OpCode == OpCodes.Call
                    && x.Operand is MethodReference
                    && new string[] { "openPort", "closePort" }.Contains((x.Operand as MethodReference).Name))
                )
            {
                var mr = ins.Operand as MemberReference;
                if (mr.Name == "closePort")
                {
                    ins.Operand = _asm.MainModule.Import(closeCallback);
                }
                else
                {
                    ins.Operand = _asm.MainModule.Import(openCallback);
                }
            }
        }

        public void FixEntryPoint()
        {
            var staticConstructor = Terraria.ProgramServer.Methods.Single(x => x.Name == "Main");

            var il = staticConstructor.Body.GetILProcessor();
            var counting = 0;
            for (var x = 0; x < staticConstructor.Body.Instructions.Count; x++)
            {
                var ins = staticConstructor.Body.Instructions[x];
                if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference && (ins.Operand as MethodReference).Name == "GetCurrentProcess")
                {
                    counting = 5;
                }

                if (counting-- > 0)
                {
                    il.Remove(ins);
                    x--;
                }
            }
        }

        public void FixSavePath()
        {
            var staticConstructor = Terraria.Main.Methods.Single(x => x.Name == ".cctor");

            var il = staticConstructor.Body.GetILProcessor();
            var removing = false;
            for (var x = 0; x < staticConstructor.Body.Instructions.Count; x++)
            {
                var ins = staticConstructor.Body.Instructions[x];
                if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference && (ins.Operand as MethodReference).Name == "GetFolderPath")
                {
                    //Remove parameters for this
                    for (var y = 0; y < 8; y++)
                    {
                        il.Remove(staticConstructor.Body.Instructions[x - 1]);
                        x--;
                    }

                    removing = true;
                }

                if (ins.OpCode == OpCodes.Stsfld && ins.Operand is FieldDefinition && (ins.Operand as FieldDefinition).Name == "SavePath")
                {
                    //Insert the new value
                    var dir = _asm.MainModule.Import(API.Patches.Methods.Single(k => k.Name == "GetCurrentDirectory"));

                    il.InsertBefore(ins, il.Create(OpCodes.Call, dir));
                    removing = false;
                    return;
                }

                if (removing)
                {
                    il.Remove(ins);
                    x--;
                }
            }
        }

        public void SkipMenu()
        {
            var initialise = Terraria.Main.Methods.Single(x => x.Name == "Initialize");
            var loc = initialise.Body.Instructions
                .Where(x => x.OpCode == OpCodes.Ldsfld && x.Operand is FieldDefinition)
                //.Select(x => x.Operand as FieldDefinition)
                .Single(x => (x.Operand as FieldDefinition).Name == "skipMenu");
            var il = initialise.Body.GetILProcessor();
            il.InsertBefore(loc, il.Create(OpCodes.Ret));
        }

        /// <summary>
        /// Adds our command line hook so we get input control from the admin
        /// </summary>
        public void PatchCommandLine()
        {
            //Simply switch to ours
            var serv = Terraria.Main.Methods.Single(x => x.Name == "DedServ");

            var callback = API.UserInput.Methods.First(m => m.Name == "ListenForCommands");

            var ins = serv.Body.Instructions
                .Single(x => x.OpCode == OpCodes.Call && x.Operand is MethodReference && (x.Operand as MethodReference).Name == "startDedInput");
            ins.Operand = _asm.MainModule.Import(callback);

            var ignore = new string[] {
				"Terraria.Main.DedServ"
			};

            //Patch Console.WriteLines
            var cwi = _asm.MainModule.Types
                .SelectMany(x => x.Methods)
                .Where(x => x.HasBody && x.Body.Instructions.Count > 0 && !ignore.Contains(x.DeclaringType.FullName + "." + x.Name))
                .SelectMany(x => x.Body.Instructions)
                .Where(x => x.OpCode == OpCodes.Call && x.Operand is MethodReference
                    && (x.Operand as MethodReference).Name == "WriteLine"
                    && (x.Operand as MethodReference).DeclaringType.FullName == "System.Console")
                .ToArray();

            foreach (var oci in cwi)
            {
                var mr = oci.Operand as MethodReference;
                var writeline = API.Tools.Methods.First(m => m.Name == "WriteLine"
                    && CompareParameters(m.Parameters, mr.Parameters));
                oci.Operand = _asm.MainModule.Import(writeline);
            }
        }

        static bool CompareParameters(Mono.Collections.Generic.Collection<ParameterDefinition> a, Mono.Collections.Generic.Collection<ParameterDefinition> b)
        {
            if (a.Count == b.Count)
            {

                for (var x = 0; x < a.Count; x++)
                {
                    if (a[x].ParameterType.FullName != b[x].ParameterType.FullName) return false;
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// Makes the types public.
        /// </summary>
        /// <param name="server">If set to <c>true</c> server.</param>
        public void MakeTypesPublic(bool server)
        {
            var types = _asm.MainModule.Types
                .Where(x => x.IsPublic == false)
                .ToArray();

            for (var x = 0; x < types.Length; x++)
                types[x].IsPublic = true;

            var sd = Terraria.WorldGen.Fields
                .Where(x => x.Name == "stopDrops")
                .Select(x => x)
                .First();
            sd.IsPrivate = false;
            sd.IsPublic = true;

            if (server)
            {
                sd = Terraria.ProgramServer.Fields
                    .Where(x => x.Name == "Game")
                    .Select(x => x)
                    .First();
                sd.IsPrivate = false;
                sd.IsPublic = true;

                var main = Terraria.Main.Methods
                    .Where(x => x.Name == "Update")
                    .Select(x => x)
                    .First();
                main.IsFamily = false;
                main.IsPublic = true;
            }
        }

        /// <summary>
        /// Changes the tile class to a structure for less over head.
        /// </summary>
        public void ChangeTileToStruct()
        {
            var tileClass = _asm.MainModule.Types.Single(x => x.Name == "Tile");
            var refClass = _self.MainModule.Types.Single(x => x.Name == "TileData");

            var userInput = _self.MainModule.Types.Single(x => x.Name == "UserInput");
            var DefaultTile = userInput.Fields.Single(x => x.Name == "DefaultTile");

            //tileClass.BaseType = refClass.BaseType;
            //tileClass.IsSequentialLayout = true;

            //Update nulls to defaults
            //var mainClass = _asm.MainModule.Types.Single(x => x.Name == "Main");

            var defaultTile = _asm.MainModule.Import(DefaultTile);

            //////Change to struct
            tileClass.BaseType = refClass.BaseType;
            tileClass.IsSequentialLayout = true;

            //Replace != null
            var mth = _asm.MainModule.Types
                .SelectMany(x => x.Methods

                    .Where(k => k.Body != null && k.Body.GetILProcessor().Body != null && k.Body.GetILProcessor().Body.Instructions.Where(i => i.OpCode == OpCodes.Ldsfld
                        && ((FieldReference)i.Operand).DeclaringType.FullName == Terraria.Main.FullName
                        && ((FieldReference)i.Operand).Name == "tile"
                        && i.Next.Next.Next.OpCode == OpCodes.Call).Count() > 0)
                )
                .ToArray();
            foreach (var item in mth)
            {
                try
                {
                    var proc = item.Body.GetILProcessor();
                    var iuns = proc.Body.Instructions
                            .Where(k => k.OpCode == OpCodes.Ldsfld
                                && ((FieldReference)k.Operand).DeclaringType.FullName == Terraria.Main.FullName
                                && ((FieldReference)k.Operand).Name == "tile"
                                && k.Next.Next.Next.OpCode == OpCodes.Call)
                            .ToArray();
                    var itm = iuns[0].Next.Next.Next;
                    proc.InsertAfter(itm, proc.Create(OpCodes.Ceq));
                    proc.InsertAfter(itm, proc.Create(OpCodes.Ldsfld, defaultTile));
                }
                catch { }
            }

            //Replace = null
            var setToNull = _asm.MainModule.Types
                .SelectMany(x => x.Methods

                    .Where(k => k.Body != null && k.Body.GetILProcessor().Body != null && k.Body.GetILProcessor().Body.Instructions.Where(i => i.OpCode == OpCodes.Ldsfld
                        && ((FieldReference)i.Operand).DeclaringType.FullName == Terraria.Main.FullName
                        && ((FieldReference)i.Operand).Name == "tile"
                        && i.OpCode == OpCodes.Ldsfld
                        && i.Next.Next.Next.OpCode == OpCodes.Ldnull).Count() > 0)
                )
                .ToArray();
            foreach (var item in setToNull)
            {
                try
                {
                    var proc = item.Body.GetILProcessor();
                    var iuns = proc.Body.Instructions
                            .Where(k => k.OpCode == OpCodes.Ldsfld
                                && ((FieldReference)k.Operand).DeclaringType.FullName == Terraria.Main.FullName
                                && ((FieldReference)k.Operand).Name == "tile"
                                && k.Next.Next.Next.OpCode == OpCodes.Ldnull)
                            .ToArray();
                    var itm = iuns[0].Next.Next.Next;
                    proc.InsertBefore(itm, proc.Create(OpCodes.Ldsfld, defaultTile));
                    proc.Remove(itm);
                }
                catch { }
            }



            var tl = _asm.MainModule.Types.Single(x => x.Name == "Tile");
            MethodDefinition opInequality, opEquality;
            //Add operators that call a static API function for comparisions


            //Do == operator
            var boolType = _asm.MainModule.Import(typeof(Boolean));
            //var ui = _self.MainModule.Types.Single(x => x.Name == "UserInput");
            var method = new MethodDefinition("op_Equality",
                                                  MethodAttributes.Public |
                                                  MethodAttributes.Static |
                                                  MethodAttributes.HideBySig |
                                                  MethodAttributes.SpecialName, boolType);

            method.Parameters.Add(new ParameterDefinition("t1", ParameterAttributes.None, tl));
            method.Parameters.Add(new ParameterDefinition("t2", ParameterAttributes.None, tl));


            var callback = API.UserInput.Methods.Single(x => x.Name == "Tile_Equality");

            var il = method.Body.GetILProcessor();

            method.Body.Instructions.Add(il.Create(OpCodes.Nop));
            method.Body.Instructions.Add(il.Create(OpCodes.Ldarg_0));
            method.Body.Instructions.Add(il.Create(OpCodes.Ldarg_1));
            method.Body.Instructions.Add(il.Create(OpCodes.Call, _asm.MainModule.Import(callback)));
            method.Body.Instructions.Add(il.Create(OpCodes.Stloc_0));

            var val = il.Create(OpCodes.Ldloc_0);
            method.Body.Instructions.Add(val);
            method.Body.Instructions.Add(il.Create(OpCodes.Ret));

            var br = il.Create(OpCodes.Br, val);
            il.InsertBefore(val, br);

            //We're storing one local variable
            method.Body.Variables.Add(new VariableDefinition(boolType));

            opEquality = method;
            tl.Methods.Add(method);

            //Do != operator
            method = new MethodDefinition("op_Inequality",
                                                  MethodAttributes.Public |
                                                  MethodAttributes.Static |
                                                  MethodAttributes.HideBySig |
                                                  MethodAttributes.SpecialName, boolType);

            method.Parameters.Add(new ParameterDefinition("t1", ParameterAttributes.None, tl));
            method.Parameters.Add(new ParameterDefinition("t2", ParameterAttributes.None, tl));


            callback = API.UserInput.Methods.Single(x => x.Name == "Tile_Inequality");

            il = method.Body.GetILProcessor();

            method.Body.Instructions.Add(il.Create(OpCodes.Nop));
            method.Body.Instructions.Add(il.Create(OpCodes.Ldarg_0));
            method.Body.Instructions.Add(il.Create(OpCodes.Ldarg_1));
            method.Body.Instructions.Add(il.Create(OpCodes.Call, _asm.MainModule.Import(callback)));
            method.Body.Instructions.Add(il.Create(OpCodes.Stloc_0));

            val = il.Create(OpCodes.Ldloc_0);
            method.Body.Instructions.Add(val);
            method.Body.Instructions.Add(il.Create(OpCodes.Ret));

            br = il.Create(OpCodes.Br, val);
            il.InsertBefore(val, br);

            //We're storing one local variable
            method.Body.Variables.Add(new VariableDefinition(boolType));

            opInequality = method;
            tl.Methods.Add(method);

            //Now to change how tiles are accessed.
            //Change to by-reference when creating a new tile for the tile array
            //Replace callvirt with call for each method of tile
            //br.s should be replaced with br after instanciation
            //ceq (and other comparitors) must now be replaced with call, and to the appropriate operator
            //Add nop's

            //By ref.
            //            var byRef = new TypeReference(tl.Namespace, tl.Name, _asm.MainModule, tl.Scope, true);
            //            var byRef = new ByReferenceType(new TypeSpecification(tl)
            //                                            {
            //
            //            });

            var mda = new ArrayType(tl);
            mda.Dimensions.Clear();
            mda.Dimensions.Insert(0, new ArrayDimension(0, null));
            mda.Dimensions.Insert(0, new ArrayDimension(0, null));
            //            mda.Dimensions.Add(new ArrayDimension(0,0));
            var byRef = mda;

            foreach (var mtd in _asm.MainModule.Types
                     .SelectMany(x => x.Methods)
                     .Where(y => y.Body != null && y.Body.Instructions.Where(z =>
                                                  z.OpCode == OpCodes.Call
                                                  && z.Operand is MethodReference
                                                  && (z.Operand as MethodReference).DeclaringType.FullName.Contains("Terraria.Tile")
                                                  ).Count() > 0))
            {
                var instructions = mtd.Body.Instructions.Where(z =>
                                                             z.OpCode == OpCodes.Call
                                                             && z.Operand is MethodReference

                                                               && (z.Operand as MethodReference).DeclaringType.FullName.Contains("Terraria.Tile")).ToArray();
                var mil = mtd.Body.GetILProcessor();
                foreach (var ins in instructions)
                {
                    var mref = (ins.Operand as MethodReference);
                    mref.DeclaringType = byRef;

                    if (mref.Name == "Get" && ins.Next.Next.OpCode == OpCodes.Ceq && ins.Next.Next.Next.OpCode == OpCodes.Brtrue_S)
                    {
                        //                        ins.Next.Next = mil.Create(OpCodes.Call, opInequality);
                        //                        ins.Next.Next.Next = mil.Create(OpCodes.Brfalse, ins.Next.Next.Next.Operand as Instruction);
                        mil.Replace(ins.Next.Next, mil.Create(OpCodes.Call, opInequality));
                        mil.Replace(ins.Next.Next.Next, mil.Create(OpCodes.Brfalse, ins.Next.Next.Next.Operand as Instruction));
                    }
                }
            }

            //Section 2 for inequality
            foreach (var mtd in _asm.MainModule.Types
                     .SelectMany(x => x.Methods)
                     .Where(y => y.Body != null && y.Body.Instructions.Where(z =>
                                                                    z.OpCode == OpCodes.Newobj
                                                                    && z.Operand is MethodReference
                                                                    && (z.Operand as MethodReference).DeclaringType.FullName == ("Terraria.Tile")
                                                                    ).Count() > 0))
            {
                var instructions = mtd.Body.Instructions.Where(z =>
                                                               z.OpCode == OpCodes.Newobj
                                                               && z.Operand is MethodReference
                                                               && (z.Operand as MethodReference).DeclaringType.FullName == ("Terraria.Tile")
                                                               ).ToArray();
                var mil = mtd.Body.GetILProcessor();
                foreach (var ins in instructions)
                {

                }
            }
        }

        /// <summary>
        /// Removes the references to the XNA binaries, and replaces them with dummies.
        /// </summary>
        public void PatchXNA(bool server)
        {
            var xnaFramework = _asm.MainModule.AssemblyReferences
                .Where(x => x.Name.StartsWith("Microsoft.Xna.Framework"))
                .ToArray();

            if (server)
                for (var x = 0; x < xnaFramework.Length; x++)
                {
                    xnaFramework[x].Name = _self.Name.Name;
                    xnaFramework[x].PublicKey = _self.Name.PublicKey;
                    xnaFramework[x].PublicKeyToken = _self.Name.PublicKeyToken;
                    xnaFramework[x].Version = _self.Name.Version;
                }
            else
            {
                for (var x = 0; x < xnaFramework.Length; x++)
                {
                    xnaFramework[x].Name = "MonoGame.Framework";
                    xnaFramework[x].PublicKey = null;
                    xnaFramework[x].PublicKeyToken = null;
                    xnaFramework[x].Version = new Version("3.1.2.0");
                }

                //Use an NSApplication entry point for MAC
            }
        }

        public void HookMessageBuffer()
        {
            var getData = Terraria.MessageBuffer.Methods.Single(x => x.Name == "GetData");
            var whoAmI = Terraria.MessageBuffer.Fields.Single(x => x.Name == "whoAmI");

            var insertionPoint = getData.Body.Instructions
                .Single(x => x.OpCode == OpCodes.Callvirt
                    && x.Operand is MethodReference
                    && (x.Operand as MethodReference).Name == "set_Position");

            var callback = API.MessageBufferCallback.Methods.First(m => m.Name == "ProcessPacket");

            var il = getData.Body.GetILProcessor();
            il.InsertAfter(insertionPoint, il.Create(OpCodes.Stloc_0));
            il.InsertAfter(insertionPoint, il.Create(OpCodes.Call, _asm.MainModule.Import(callback)));
            il.InsertAfter(insertionPoint, il.Create(OpCodes.Ldloc_0));
            il.InsertAfter(insertionPoint, il.Create(OpCodes.Ldfld, whoAmI));
            il.InsertAfter(insertionPoint, il.Create(OpCodes.Ldarg_0));
        }

        public void RemoveClientCode()
        {
            var methods = _asm.MainModule.Types
                .SelectMany(x => x.Methods)
                .ToArray();
            var offsets = new System.Collections.Generic.List<Instruction>();

            foreach (var mth in methods)
            {
                var hasMatch = true;
                while (mth.HasBody && hasMatch)
                {
                    var match = mth.Body.Instructions
                        .SingleOrDefault(x => x.OpCode == OpCodes.Ldsfld
                            && x.Operand is FieldReference
                            && (x.Operand as FieldReference).Name == "netMode"
                            && x.Next.OpCode == OpCodes.Ldc_I4_1
                            && (x.Next.Next.OpCode == OpCodes.Bne_Un_S) // || x.Next.Next.OpCode == OpCodes.Bne_Un)
                            && !offsets.Contains(x)
                            && (x.Previous == null || x.Previous.OpCode != OpCodes.Bne_Un_S));

                    hasMatch = match != null;
                    if (hasMatch)
                    {
                        var blockEnd = match.Next.Next.Operand as Instruction;
                        var il = mth.Body.GetILProcessor();

                        var cur = il.Body.Instructions.IndexOf(match) + 3;

                        while (il.Body.Instructions[cur] != blockEnd)
                        {
                            il.Remove(il.Body.Instructions[cur]);
                        }
                        offsets.Add(match);
                        //var newIns = il.Body.Instructions[cur];
                        //for (var x = 0; x < il.Body.Instructions.Count; x++)
                        //{
                        //    if (il.Body.Instructions[x].Operand == newIns)
                        //    {
                        //        il.Replace(il.Body.Instructions[x], il.Create(il.Body.Instructions[x].OpCode, newIns));
                        //    }
                        //}
                    }
                }
            }
        }

        public void HookSockets()
        {
            var targetField = API.IAPISocket.Fields.Single(x => x.Name == "tileSection");

            //Replace Terraria.Netplay.serverSock references with tdsm.core.Server.slots
            var instructions = _asm.MainModule.Types
                .SelectMany(x => x.Methods
                    .Where(y => y.HasBody && y.Body.Instructions != null)
                )
                .SelectMany(x => x.Body.Instructions)
                .Where(x => x.OpCode == Mono.Cecil.Cil.OpCodes.Ldsfld
                    && x.Operand is FieldReference
                    && (x.Operand as FieldReference).FieldType.FullName == "Terraria.ServerSock[]"
                    && x.Next.Next.Next.OpCode == Mono.Cecil.Cil.OpCodes.Ldfld
                    && x.Next.Next.Next.Operand is FieldReference
                    && (x.Next.Next.Next.Operand as FieldReference).Name == "tileSection"
                )
                .ToArray();

            for (var x = 0; x < instructions.Length; x++)
            {
                //instructions[x].Operand = _asm.MainModule.Import(targetArray);
                instructions[x].Next.Next.Next.Operand = _asm.MainModule.Import(targetField);
            }


            //TODO BELOW - update ServerSock::announce to IAPISocket::announce (etc)
            //Replace Terraria.Netplay.serverSock references with tdsm.core.Server.slots
            //instructions = _asm.MainModule.Types
            //   .SelectMany(x => x.Methods
            //       .Where(y => y.HasBody && y.Body.Instructions != null)
            //   )
            //   .SelectMany(x => x.Body.Instructions)
            //   .Where(x => x.OpCode == Mono.Cecil.Cil.OpCodes.Ldsfld
            //       && x.Operand is FieldReference
            //       && (x.Operand as FieldReference).FieldType.FullName == "Terraria.ServerSock[]"
            //   )
            //   .ToArray();

            //for (var x = 0; x < instructions.Length; x++)
            //{
            //    instructions[x].Operand = _asm.MainModule.Import(targetArray);

            //    //var var = instructions[x].Next.Next.Next;
            //    //if (var.OpCode == OpCodes.Ldfld && var.Operand is MemberReference)
            //    //{
            //    //    var mem = var.Operand as MemberReference;
            //    //    if (mem.DeclaringType.Name == "ServerSock")
            //    //    {
            //    //        var ourVar = sockClass.Fields.Where(j => j.Name == mem.Name).FirstOrDefault();
            //    //        if (ourVar != null)
            //    //        {
            //    //            var.Operand = _asm.MainModule.Import(ourVar);
            //    //        }
            //    //    }
            //    //}
            //}

            instructions = _asm.MainModule.Types
               .SelectMany(x => x.Methods
                   .Where(y => y.HasBody && y.Body.Instructions != null)
               )
               .SelectMany(x => x.Body.Instructions)
               .Where(x => (x.OpCode == Mono.Cecil.Cil.OpCodes.Callvirt)
                   &&
                   (
                        (x.Operand is MemberReference && (x.Operand as MemberReference).DeclaringType.FullName == "Terraria.ServerSock")
                        ||
                        (x.Operand is MethodDefinition && (x.Operand as MethodDefinition).DeclaringType.FullName == "Terraria.ServerSock")
                   )
               )
               .ToArray();

            instructions = _asm.MainModule.Types
               .SelectMany(x => x.Methods
                   .Where(y => y.HasBody && y.Body.Instructions != null)
               )
               .SelectMany(x => x.Body.Instructions)
               .Where(x => (x.OpCode == Mono.Cecil.Cil.OpCodes.Ldfld || x.OpCode == Mono.Cecil.Cil.OpCodes.Stfld || x.OpCode == Mono.Cecil.Cil.OpCodes.Callvirt)
                   &&
                   (
                        (x.Operand is MemberReference && (x.Operand as MemberReference).DeclaringType.FullName == "Terraria.ServerSock")
                        ||
                        (x.Operand is MethodDefinition && (x.Operand as MethodDefinition).DeclaringType.FullName == "Terraria.ServerSock")
                   )
               )
               .ToArray();

            for (var x = 0; x < instructions.Length; x++)
            {
                var var = instructions[x];
                if (var.Operand is MethodDefinition)
                {
                    var mth = var.Operand as MethodDefinition;
                    var ourVar = API.IAPISocket.Methods.SingleOrDefault(j => j.Name == mth.Name);
                    if (ourVar != null)
                    {
                        var.Operand = _asm.MainModule.Import(ourVar);
                    }
                }
                else if (var.Operand is MemberReference)
                {
                    var mem = var.Operand as MemberReference;
                    var ourVar = API.IAPISocket.Fields.SingleOrDefault(j => j.Name == mem.Name);
                    if (ourVar != null)
                    {
                        var.Operand = _asm.MainModule.Import(ourVar);
                    }
                }
            }

            foreach (var rep in new string[] { /*"SendAnglerQuest", "sendWater", "syncPlayers",*/ "AddBan" })
            {
                var toBeReplaced = _asm.MainModule.Types
                    .SelectMany(x => x.Methods
                        .Where(y => y.HasBody)
                    )
                    .SelectMany(x => x.Body.Instructions)
                    .Where(x => x.OpCode == Mono.Cecil.Cil.OpCodes.Call
                        && x.Operand is MethodReference
                        && (x.Operand as MethodReference).Name == rep
                    )
                    .ToArray();

                var replacement = API.NetplayCallback.Methods.Single(x => x.Name == rep);
                for (var x = 0; x < toBeReplaced.Length; x++)
                {
                    toBeReplaced[x].Operand = _asm.MainModule.Import(replacement);
                }
            }

            //Change to override
            Terraria.ServerSock.BaseType = _asm.MainModule.Import(API.IAPISocket);
            foreach (var rep in new string[] { "SpamUpdate", "SpamClear", "Reset", "SectionRange" })
            {
                var mth = Terraria.ServerSock.Methods.Single(x => x.Name == rep);
                mth.IsVirtual = true;
            }

            //Remove variables that are in the base class
            foreach (var fld in API.IAPISocket.Fields)
            {
                var rem = Terraria.ServerSock.Fields.SingleOrDefault(x => x.Name == fld.Name);
                if (rem != null)
                {
                    Terraria.ServerSock.Fields.Remove(rem);
                }
            }

            //Now change Netplay.serverSock to the IAPISocket type
            var serverSockArr = Terraria.Netplay.Fields.Single(x => x.Name == "serverSock");
            var at = new ArrayType(_asm.MainModule.Import(API.IAPISocket));
            serverSockArr.FieldType = at;

            var sendWater = Terraria.NetMessage.Methods.Single(x => x.Name == "sendWater");
            {
                var ix = 0;
                var removing = false;
                while (sendWater.Body.Instructions.Count > 0 && ix < sendWater.Body.Instructions.Count)
                {
                    var first = false;
                    var ins = sendWater.Body.Instructions[ix];
                    if (ins.OpCode == OpCodes.Ldsfld && ins.Operand is FieldReference && (ins.Operand as FieldReference).Name == "buffer")
                    {
                        removing = true;
                        first = true;
                    }
                    else first = false;

                    if (ins.OpCode == OpCodes.Brfalse_S)
                    {
                        //Keep instruction, and replace the first (previous instruction)
                        var canSendWater = API.IAPISocket.Methods.Single(x => x.Name == "CanSendWater");

                        var il = sendWater.Body.GetILProcessor();
                        var target = ins.Previous;
                        var newTarget = il.Create(OpCodes.Nop);

                        il.Replace(target, newTarget);

                        il.InsertAfter(newTarget, il.Create(OpCodes.Callvirt, _asm.MainModule.Import(canSendWater)));
                        il.InsertAfter(newTarget, il.Create(OpCodes.Ldelem_Ref));
                        il.InsertAfter(newTarget, il.Create(OpCodes.Ldloc_0));
                        il.InsertAfter(newTarget, il.Create(OpCodes.Ldsfld, _asm.MainModule.Import(serverSockArr)));

                        removing = false;
                        break;
                    }

                    if (removing && !first)
                    {
                        sendWater.Body.Instructions.RemoveAt(ix);
                    }

                    if (!removing || first) ix++;
                }
            }

            var syncPlayers = Terraria.NetMessage.Methods.Single(x => x.Name == "syncPlayers");
            {
                var ix = 0;
                var removing = false;
                var isPlayingComplete = false;
                while (syncPlayers.Body.Instructions.Count > 0 && ix < syncPlayers.Body.Instructions.Count)
                {
                    var first = false;
                    var ins = syncPlayers.Body.Instructions[ix];
                    if (ins.OpCode == OpCodes.Ldsfld && ins.Operand is FieldDefinition && (ins.Operand as FieldDefinition).Name == "serverSock")
                    {
                        removing = true;
                        first = true;

                        if (isPlayingComplete)
                        {
                            //We'll use the next two instructions because im cheap.
                            ix += 2;
                        }
                    }
                    else first = false;

                    if (removing && ins.OpCode == OpCodes.Bne_Un)
                    {
                        //Keep instruction, and replace the first (previous instruction)
                        var isPlaying = API.IAPISocket.Methods.Single(x => x.Name == "IsPlaying");

                        var il = syncPlayers.Body.GetILProcessor();
                        var target = ins.Previous;

                        il.InsertAfter(target, il.Create(OpCodes.Callvirt, _asm.MainModule.Import(isPlaying)));
                        il.InsertAfter(target, il.Create(OpCodes.Ldelem_Ref));
                        il.InsertAfter(target, il.Create(OpCodes.Ldloc_1));

                        il.Replace(ins, il.Create(OpCodes.Brfalse, ins.Operand as Instruction));

                        isPlayingComplete = true;
                        removing = false;
                        //break;

                        ix += 3;
                    }
                    else if (removing && ins.OpCode == OpCodes.Callvirt && isPlayingComplete)
                    {
                        if (ins.Operand is MethodReference)
                        {
                            var md = ins.Operand as MethodReference;
                            if (md.DeclaringType.Name == "Object" && md.Name == "ToString")
                            {
                                var remoteAddress = API.IAPISocket.Methods.Single(x => x.Name == "RemoteAddress");
                                ins.Operand = _asm.MainModule.Import(remoteAddress);
                                break;
                            }
                        }
                    }

                    if (removing && !first)
                    {
                        syncPlayers.Body.Instructions.RemoveAt(ix);
                    }

                    if (!removing || first) ix++;
                }
            }

            var sendAngler = Terraria.NetMessage.Methods.Single(x => x.Name == "SendAnglerQuest");
            {
                var ix = 0;
                var removing = false;
                while (sendAngler.Body.Instructions.Count > 0 && ix < sendAngler.Body.Instructions.Count)
                {
                    var first = false;
                    var ins = sendAngler.Body.Instructions[ix];
                    if (ins.OpCode == OpCodes.Ldsfld && ins.Operand is FieldDefinition && (ins.Operand as FieldDefinition).Name == "serverSock")
                    {
                        removing = true;
                        first = true;

                        //Reuse the next two as well
                        ix += 2;
                    }
                    else first = false;

                    if (removing && ins.OpCode == OpCodes.Bne_Un_S)
                    {
                        //Keep instruction, and replace the first (previous instruction)
                        var isPlaying = API.IAPISocket.Methods.Single(x => x.Name == "IsPlaying");

                        var il = sendAngler.Body.GetILProcessor();

                        il.InsertBefore(ins, il.Create(OpCodes.Callvirt, _asm.MainModule.Import(isPlaying)));
                        il.Replace(ins, il.Create(OpCodes.Brfalse, ins.Operand as Instruction));

                        removing = false;
                        break;
                    }

                    if (removing && !first)
                    {
                        sendAngler.Body.Instructions.RemoveAt(ix);
                    }

                    if (!removing || first) ix++;
                }
            }
        }

        public void HookNPCSpawning()
        {
            var newNPC = Terraria.NPC.Methods.Single(x => x.Name == "NewNPC");
            var method = API.NPCCallback.Methods.Single(x => x.Name == "CanSpawnNPC");

            var il = newNPC.Body.GetILProcessor();
            var first = newNPC.Body.Instructions.First();

            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_2));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_3));
            il.InsertBefore(first, il.Create(OpCodes.Call, _asm.MainModule.Import(method)));

            il.InsertBefore(first, il.Create(OpCodes.Brtrue_S, first));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4, 200));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
        }

        public void HookEclipse()
        {
            var mth = Terraria.Main.Methods.Single(x => x.Name == "UpdateTime");
            var field = API.MainCallback.Fields.Single(x => x.Name == "StartEclipse");

            var il = mth.Body.GetILProcessor();
            var start = il.Body.Instructions.Single(x =>
                x.OpCode == OpCodes.Ldsfld
                && x.Operand is FieldReference
                && (x.Operand as FieldReference).Name == "hardMode"
                && x.Previous.OpCode == OpCodes.Call
                && x.Previous.Operand is MethodReference
                && (x.Previous.Operand as MethodReference).Name == "StartInvasion"
            );

            //Preserve
            var old = start.Operand as FieldReference;

            //Replace with ours to keep the IL inline
            start.Operand = _asm.MainModule.Import(field);
            //Readd the preserved
            il.InsertAfter(start, il.Create(OpCodes.Ldsfld, old));

            //Now find the target instruction if the value is true
            var startEclipse = il.Body.Instructions.Single(x =>
                x.OpCode == OpCodes.Stsfld
                && x.Operand is FieldReference
                && (x.Operand as FieldReference).Name == "eclipse"
                && x.Next.OpCode == OpCodes.Ldsfld
                && x.Next.Operand is FieldReference
                && (x.Next.Operand as FieldReference).Name == "eclipse"
            ).Previous;
            il.InsertAfter(start, il.Create(OpCodes.Brtrue, startEclipse));

            //Since all we care about is setting the StartEclipse to TRUE; we need to be able to disable once done.
            il.InsertAfter(startEclipse.Next, il.Create(OpCodes.Stsfld, start.Operand as FieldReference));
            il.InsertAfter(startEclipse.Next, il.Create(OpCodes.Ldc_I4_0));
        }

        public void HookBloodMoon()
        {
            var mth = Terraria.Main.Methods.Single(x => x.Name == "UpdateTime");
            var field = API.MainCallback.Fields.Single(x => x.Name == "StartBloodMoon");
            //return;
            var il = mth.Body.GetILProcessor();
            var start = il.Body.Instructions.Single(x =>
                x.OpCode == OpCodes.Ldsfld
                && x.Operand is FieldReference
                && (x.Operand as FieldReference).Name == "spawnEye"
                && x.Next.Next.OpCode == OpCodes.Ldsfld
                && x.Next.Next.Operand is FieldReference
                && (x.Next.Next.Operand as FieldReference).Name == "moonPhase"
            );

            //Preserve
            var old = start.Operand as FieldReference;
            var target = start.Next as Instruction;

            //Replace with ours to keep the IL inline
            start.Operand = _asm.MainModule.Import(field);
            //Readd the preserved
            il.InsertAfter(start, il.Create(OpCodes.Ldsfld, old));

            //Now find the target instruction if the value is true
            Instruction begin = start.Next;
            var i = 12;
            while (i > 0)
            {
                i--;
                begin = begin.Next;
            }
            il.InsertAfter(start, il.Create(OpCodes.Brtrue, begin));

            //Since all we care about is setting the StartBloodMoon to TRUE; we need to be able to disable once done.
            var startBloodMoon = il.Body.Instructions.Single(x =>
                x.OpCode == OpCodes.Ldsfld
                && x.Operand is FieldReference
                && (x.Operand as FieldReference).Name == "bloodMoon"
                && x.Next.Next.OpCode == OpCodes.Ldsfld
                && x.Next.Next.Operand is FieldReference
                && (x.Next.Next.Operand as FieldReference).Name == "netMode"
            );
            il.InsertAfter(startBloodMoon.Next, il.Create(OpCodes.Stsfld, start.Operand as FieldReference));
            il.InsertAfter(startBloodMoon.Next, il.Create(OpCodes.Ldc_I4_0));
        }

        public void Save(string fileName, int apiBuild, string tdsmUID, string name)
        {
            //Ensure the name is updated to the new one
            _asm.Name = new AssemblyNameDefinition(name, new Version(0, 0, apiBuild, 0));
            _asm.MainModule.Name = fileName;

            //Change the uniqueness from what Terraria has, to something different (that way vanilla isn't picked up by assembly resolutions)
            var g = _asm.CustomAttributes.Single(x => x.AttributeType.Name == "GuidAttribute");

            for (var x = 0; x < _asm.CustomAttributes.Count; x++)
            {
                if (_asm.CustomAttributes[x].AttributeType.Name == "GuidAttribute")
                {
                    _asm.CustomAttributes[x].ConstructorArguments[0] =
                        new CustomAttributeArgument(_asm.CustomAttributes[x].ConstructorArguments[0].Type, tdsmUID);
                }
                else if (_asm.CustomAttributes[x].AttributeType.Name == "AssemblyTitleAttribute")
                {
                    _asm.CustomAttributes[x].ConstructorArguments[0] =
                        new CustomAttributeArgument(_asm.CustomAttributes[x].ConstructorArguments[0].Type, name);
                }
                else if (_asm.CustomAttributes[x].AttributeType.Name == "AssemblyProductAttribute")
                {
                    _asm.CustomAttributes[x].ConstructorArguments[0] =
                        new CustomAttributeArgument(_asm.CustomAttributes[x].ConstructorArguments[0].Type, name);
                }
                //else if (_asm.CustomAttributes[x].AttributeType.Name == "AssemblyFileVersionAttribute")
                //{
                //    _asm.CustomAttributes[x].ConstructorArguments[0] =
                //        new CustomAttributeArgument(_asm.CustomAttributes[x].ConstructorArguments[0].Type, "1.0.0.0");
                //}
            }

            //_asm.Write(fileName);
            using (var fs = File.OpenWrite(fileName))
            {
                _asm.Write(fs);
                fs.Flush();
                fs.Close();
            }
        }

        public void Dispose()
        {
            _self = null;
            _asm = null;
        }
    }
}
