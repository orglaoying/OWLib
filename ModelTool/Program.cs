﻿using System.IO;
using System;
using OWLib;
using System.Collections.Generic;
using System.Reflection;

namespace ModelTool {
  public class Program {
    public delegate void ModelWriteDelegate(Model model, Stream stream, List<byte> lods, bool[] opts);

    public static void Main(string[] args) {
      if(args.Length < 3) {
        Console.Out.WriteLine("Usage: ModelTool.exe 00C_file type [-l n] output_file");
        Console.Out.WriteLine("type can be:");
        Console.Out.WriteLine("  o - OBJ");
        Console.Out.WriteLine("  a - XNALara ASCII");
        Console.Out.WriteLine("  b - XNALara BIN");
        Console.Out.WriteLine("  f - Autodesk FBX 2006 ASCII");
        Console.Out.WriteLine("  F - Autodesk FBX 2006 Binary");
        Console.Out.WriteLine("args:");
        Console.Out.WriteLine("  -l n - only save LOD, where N is lod");
        Console.Out.WriteLine("  -t   - only save attachment points (only supported on OBJ)");
        return;
      }

      Console.Out.WriteLine("{0} v{1}", Assembly.GetExecutingAssembly().GetName().Name, Assembly.GetExecutingAssembly().GetName().Version.ToString());

      string modelFile = args[0];
      char type = args[1].ToLowerInvariant()[0];
      char typen = args[1][0];
      string outputFile = args[args.Length - 1];
      List<byte> lods = null;
      bool attachments = false;
      if(args.Length > 3) {
        int i = 2;
        while(i < args.Length - 2) {
          string arg = args[i];
          ++i;
          if(arg[0] == '-') {
            if(arg[1] == 'l') {
              if(lods == null) {
                lods = new List<byte>();
              }
              byte b = byte.Parse(args[i], System.Globalization.NumberStyles.Number);
              lods.Add(b);
              ++i;
            } else if(arg[1] == 't') {
              attachments = true;
            }
          } else {
            continue;
          }
        }
      }

      ModelWriteDelegate writer = null;
      if(type == 'o') {
        writer = OBJWriter.Write;
      } else if(type == 'a') {
        writer = ASCIIWriter.Write;
      } else if(type == 'b') {
        writer = BINWriter.Write;
      } else if(typen == 'f') {
        writer = FBXWriter.WriteASCII;
      } else if(typen == 'F') {
        writer = FBXWriter.WriteBIN;
      } else {
        Console.Error.WriteLine("Unknown output format {0}", type);
        return;
      }

      using(Stream modelStream = File.Open(modelFile, FileMode.Open, FileAccess.Read)) {
        Model model = new Model(modelStream);
        using(Stream outStream = File.Open(outputFile, FileMode.Create, FileAccess.Write)) {
          writer(model, outStream, lods, new bool[1] { attachments });
        }
      }
    }
  }
}