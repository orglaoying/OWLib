﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CASCExplorer;
using OWLib;

namespace OverTool {
  public class Util {
    public static void CopyBytes(Stream i, Stream o, int sz) {
      byte[] buffer = new byte[sz];
      i.Read(buffer, 0, sz);
      o.Write(buffer, 0, sz);
      buffer = null;
    }

    public static Stream OpenFile(Record record, CASCHandler handler, bool recur = true) {
      MemoryStream ms = new MemoryStream(record.record.Size);

      long offset = 0;
      EncodingEntry enc;
      if(((ContentFlags)record.record.Flags & ContentFlags.Bundle) == ContentFlags.Bundle) {
        offset = record.record.Offset;
        handler.Encoding.GetEntry(record.index.bundleContentKey, out enc);
      } else {
        handler.Encoding.GetEntry(record.record.ContentKey, out enc);
      }

      try {
        Stream fstream = handler.OpenFile(enc.Key);
        fstream.Position = offset;
        CopyBytes(fstream, ms, record.record.Size);
        ms.Position = 0;
      } catch (Exception ex) {
        if(recur) {
          OwRootHandler ow = (OwRootHandler)handler.Root;
          foreach(APMFile apm in ow.APMFiles) {
            if(!apm.Name.ToLowerInvariant().Contains("rdev")) {
              continue; // skip
            }
            for(int i = 0; i < apm.Packages.Length; ++i) {
              APMPackage package = apm.Packages[i];
              PackageIndex index = apm.Indexes[i];
              PackageIndexRecord[] records = apm.Records[i];
              for(long j = 0; j < records.LongLength; ++j) {
                PackageIndexRecord recordindex = records[j];
                if(recordindex.Key != record.record.Key) {
                  continue;
                }

                Stream strm = OpenFile(new Record {
                  package = package,
                  index = index,
                  record = recordindex,
                }, handler, false);
                if(strm != null) {
                  return strm;
                }
              }
            }
          }
          Console.Out.WriteLine("Error {0} {1:X16}", ex.Message, record.package.packageKey);
        }
        return null;
      }
      return ms;
    }

    public static string SanitizePath(string name) {
      char[] invalids = Path.GetInvalidFileNameChars();
      return string.Join("_", name.Split(invalids));
    }

    public static string SanitizeDir(string name) {
      char[] invalids = Path.GetInvalidPathChars();
      return string.Join("_", name.Split(invalids));
    }

    public static string Strip(string name) {
      return name.TrimEnd(new char[2] { '_', ' ' });
    }

    public static string GetString(ulong key, Dictionary<ulong, Record> map, CASCHandler handler) {
      if(!map.ContainsKey(key)) {
        return null;
      }

      Stream str = OpenFile(map[key], handler);
      OWString ows = new OWString(str);
      return ows.Value;
    }
  }
}
