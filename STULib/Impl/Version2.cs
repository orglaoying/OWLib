﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using OWLib;
using static STULib.Types.Generic.Common;
using static STULib.Types.Generic.Version2;
using System.Diagnostics;
using System.Runtime.InteropServices;

// ReSharper disable MemberCanBeMadeStatic.Local
namespace STULib.Impl {
    public class Version2 : ISTU {
        public override IEnumerable<STUInstance> Instances => instances.Select((pair => pair.Value));
        public override uint Version => 2;

        protected Stream stream;
        protected MemoryStream metadata;

        private ulong headerCrc = 0;

        protected Dictionary<long, STUInstance> instances;

        protected STUHeader header;
        protected STUInstanceRecord[] instanceInfo;
        protected STUInstanceArrayRef[] instanceArrayRef;
        protected STUInstanceFieldList[] instanceFieldLists;
        protected STUInstanceField[][] instanceFields;

        protected Dictionary<Array, int[]> EmbedArrayRequests;

        private uint buildVersion;

        protected BinaryReader metadataReader;

        public override void Dispose() {
            stream?.Dispose();
            metadataReader?.Dispose();
        }

        internal static bool IsValidVersion(BinaryReader reader) {
            return reader.BaseStream.Length >= 36;
        }

        public Version2(Stream stuStream, uint owVersion) {
            stream = stuStream;
            buildVersion = owVersion;
            instances = new Dictionary<long, STUInstance>();
            ReadInstanceData(stuStream.Position);
        }

        private object GetValueArray(Type type, STUFieldAttribute element, STUArray info, STUInstanceField writtenField) {
            Array array = Array.CreateInstance(type, info.Count);
            for (uint i = 0; i < info.Count; ++i) {
                if (element != null) {
                    metadata.Position += element.Padding;
                }

                uint parent = 0;
                if (instanceArrayRef?.Length > info.InstanceIndex) {
                    parent = instanceArrayRef[info.InstanceIndex].Checksum;
                }
                array.SetValue(GetValueArrayInner(type, element, parent, writtenField.FieldChecksum), i);
            }
            return array;
        }

        protected void DemangleInstance(object instance, uint checksum) {
            if (instance is IDemangleable demangleable) {
                ulong[] GUIDs = demangleable.GetGUIDs();
                ulong[] XORs = demangleable.GetGUIDXORs();
                if (GUIDs?.Length > 0) {
                    for (long j = 0; j < GUIDs.LongLength; ++j) {
                        GUIDs[j] = DemangleGUID(GUIDs[j], checksum) ^ XORs[j];
                    }
                }
                demangleable.SetGUIDs(GUIDs);
            }
        }

        private string ReadMetadataString() {
            STUString data = metadataReader.Read<STUString>();
            metadata.Position = data.Offset;
            return new string(metadataReader.ReadChars((int) data.Size));
        }
        
        // ReSharper disable once UnusedParameter.Local
        private object GetValueArrayInner(Type type, STUFieldAttribute element, uint parent, uint checksum) {
            BinaryReader reader = metadataReader;
            switch (type.Name) {
                case "String":
                    return ReadMetadataString();
                case "Single":
                    return reader.ReadSingle();
                case "Boolean":
                    return reader.ReadByte() != 0;
                case "Int16":
                    return reader.ReadInt16();
                case "UInt16":
                    return reader.ReadUInt16();
                case "Int32":
                    return reader.ReadInt32();
                case "UInt32":
                    return reader.ReadUInt32();
                case "Int64":
                    return reader.ReadInt64();
                case "UInt64":
                    return reader.ReadUInt64();
                case "Byte":
                    return reader.ReadByte();
                case "SByte":
                    return reader.ReadSByte();
                case "Object":
                    reader.BaseStream.Position += 4;
                    return null;
                case "Char":
                    return reader.ReadChar();
                default:
                    if (type.IsEnum) {
                        return Enum.ToObject(type, GetValueArrayInner(type.GetEnumUnderlyingType(), element, parent, checksum));
                    }
                    if (type.IsClass || type.IsValueType) {
                        return InitializeObject(Activator.CreateInstance(type), type, parent, checksum, reader, true);
                    }
                    return null;
            }
        }

        private object GetPrimitiveVarValue(BinaryReader reader, STUInstanceField field, Type target) {
            object value = 0;
            bool signed = Convert.ToBoolean(target.GetField("MinValue").GetValue(null));
            uint size = field.FieldSize > 0 ? field.FieldSize : (uint) Marshal.SizeOf(target);
            if (size == 16) value = reader.ReadDecimal();
            if (!signed) {
                switch (size) {
                    case 16: break;
                    case 8:
                        value = reader.ReadUInt64();
                        break;
                    case 4:
                        value = reader.ReadUInt32();
                        break;
                    case 2:
                        value = reader.ReadUInt16();
                        break;
                    case 1:
                        value = reader.ReadByte();
                        break;
                    default: throw new InvalidDataException();
                }
            } else {
                switch (size) {
                    case 16: break;
                    case 8:
                        value = reader.ReadInt64();
                        break;
                    case 4:
                        value = reader.ReadInt32();
                        break;
                    case 2:
                        value = reader.ReadInt16();
                        break;
                    case 1:
                        value = reader.ReadSByte();
                        break;
                    default: throw new InvalidDataException();
                }
            }
            return Convert.ChangeType(value, target); // if you get an OverflowException, you need to use a bigger type
        }

        // ReSharper disable once UnusedParameter.Local
        private object GetValue(STUInstanceField field, Type type, BinaryReader reader, STUFieldAttribute element) {
            if (type.IsArray) {
                return InitializeObjectArray(field, type.GetElementType(), reader, element);
            }
            switch (type.Name) {
                case "String": {
                    metadata.Position = reader.ReadUInt32();
                    return ReadMetadataString();
                }
                case "Single":
                    return reader.ReadSingle();
                case "Int16":
                case "UInt16":
                case "Int32":
                case "UInt32":
                case "Int64":
                case "UInt64":
                case "SByte":
                case "Byte":
                case "Char":
                    return GetPrimitiveVarValue(reader, field, type);
                case "Boolean":
                    return (byte)GetPrimitiveVarValue(reader, field, typeof(byte)) != 0;
                case "Object":
                    reader.BaseStream.Position += 4;
                    return null;
                default:
                    if (typeof(STUInstance).IsAssignableFrom(type)) {
                        return null; // Set later by the second pass. Usually this is uint, indicating which STU instance entry to load inorder to get the instance. However this is only useful when you're streaming specific STUs.
                    }
                    if (type.IsEnum) {
                        return GetValue(field, type.GetEnumUnderlyingType(), reader, element);
                    }
                    if (type.IsClass) {
                        return InitializeObject(Activator.CreateInstance(type), type, field.FieldChecksum, field.FieldChecksum, reader, element.FakeBuffer);
                    }
                    if (type.IsValueType) {
                        return InitializeObject(Activator.CreateInstance(type), type, CreateInstanceFields(type),
                            reader);
                    }
                    return null;
            }
        }

        private STUInstanceField[] CreateInstanceFields(Type instanceType) {
            return instanceType.GetCustomAttributes<STUOverride>()
                .Select(@override => new STUInstanceField {
                    FieldChecksum = @override.Checksum,
                    FieldSize = @override.Size
                }).ToArray();
        }

        private void SetField(object instance, uint checksum, object value) {
            if (instance == null) {
                return;
            }

            Dictionary<uint, FieldInfo> fieldMap = CreateFieldMap(GetValidFields(instance.GetType()));

            if (fieldMap.ContainsKey(checksum)) {
                fieldMap[checksum].SetValue(instance, value);
            }
        }

        protected static Dictionary<uint, FieldInfo> CreateFieldMap(IEnumerable<FieldInfo> info) {
            return info.ToDictionary(fieldInfo => fieldInfo.GetCustomAttribute<STUFieldAttribute>().Checksum);
        }

        protected static IEnumerable<FieldInfo> GetValidFields(Type instanceType) {
            return instanceType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance |
                                          BindingFlags.FlattenHierarchy)
                .Where(fieldInfo => fieldInfo.GetCustomAttribute<STUFieldAttribute>()?.Checksum > 0);
        }

        private object InitializeObjectArray(STUInstanceField field, Type type, BinaryReader reader, STUFieldAttribute element) {
            if (field.FieldSize == 0 && field.FieldChecksum != 0) {
                STUInlineArrayInfo inlineInfo = reader.Read<STUInlineArrayInfo>();
                if (inlineInfo.Size > 600000) return null; // i feel that it's unlikley that there will be an inline with this count, exceptions if this isn't checked.
                Array array = Array.CreateInstance(type, (uint)inlineInfo.Count);
                // if (inlineInfo.FieldListIndex == 0) return array;
                for (uint i = 0; i < (uint)inlineInfo.Count; ++i) {
                    stream.Position += element.Padding;
                    uint fieldIndex = reader.ReadUInt32();
                    object instance = Activator.CreateInstance(type);
                    if (fieldIndex >= instanceFieldLists.Length) continue;
                    array.SetValue(InitializeObject(instance, type, instanceFields[fieldIndex], reader), i);
                }
                return array;
            } if (typeof(STUInstance).IsAssignableFrom(type)) {
                STUArray arrayDef = new STUArray();
                int embedArrayOffset = reader.ReadInt32();
                metadata.Position = embedArrayOffset;
                int embedCount = metadataReader.ReadInt32();
                if (embedCount == 0) {
                    return null;
                }
                arrayDef.Count = (uint)embedCount;
                arrayDef.Unknown = metadataReader.ReadUInt32();
                metadata.Position = metadataReader.ReadInt64();
                
                Array array = Array.CreateInstance(type, arrayDef.Count);
                
                List<int> request = new List<int>();
                for (int i = 0; i < arrayDef.Count; i++) {
                    int value = metadataReader.ReadInt32();
                    metadataReader.ReadInt32(); // Padding for in-place deserialization
                    if (value == -1) return null;
                    request.Add(value);
                }
                EmbedArrayRequests[array] = request.ToArray();
                return array;
            }
            int offset = reader.ReadInt32();
            metadata.Position = offset;
            STUArray arrayInfo = metadataReader.Read<STUArray>();
            metadata.Position = arrayInfo.Offset;
            return GetValueArray(type, element, arrayInfo, field);
        }

        private object InitializeObject(object instance, Type type, uint reference, uint checksum, BinaryReader reader, bool isArrayBuffer) {
            FieldInfo[] fields = GetFields(type);
            foreach (FieldInfo field in fields) {
                STUFieldAttribute element = field.GetCustomAttribute<STUFieldAttribute>();
                bool skip = false;
                if (element?.STUVersionOnly != null) {
                    skip = !element.STUVersionOnly.Any(version => version == Version);
                }
                if (element?.IgnoreVersion != null && skip) {
                    skip = !element.IgnoreVersion.Any(stufield => stufield == reference);
                }
                if (skip) {
                    continue;
                }
                if (!CheckCompatVersion(field, buildVersion)) {
                    continue;
                } 
                if (element?.OnlyBuffer == true && !isArrayBuffer) {
                    continue;
                }
                if (field.FieldType.IsArray) {
                    field.SetValue(instance, InitializeObjectArray(new STUInstanceField { FieldChecksum = 0, FieldSize = 4 }, field.FieldType, reader, element));
                }
                else {
                    long position = -1;
                    if (element?.ReferenceValue == true) {
                        long offset = reader.ReadInt64();
                        if (offset == 0) {
                            continue;
                        }
                        position = reader.BaseStream.Position;
                        reader.BaseStream.Position = offset;
                    }
                    field.SetValue(instance, GetValue(new STUInstanceField {
                            FieldChecksum = 0,
                            FieldSize = element == null || element.DummySize == -1 ? 4 : (uint) element.DummySize
                        },
                        field.FieldType, reader, element));
                    if (position > -1) {
                        reader.BaseStream.Position = position;
                    }
                }
                if (element?.Verify != null) {
                    if (!field.GetValue(instance).Equals(element.Verify)) {
                        throw new Exception("Verification failed");
                    }
                }
            }
            DemangleInstance(instance, checksum);

            return instance;
        }

        protected virtual object InitializeObject(object instance, Type type, STUInstanceField[] writtenFields,
            BinaryReader reader) {
            Dictionary<uint, FieldInfo> fieldMap = CreateFieldMap(GetValidFields(type));

            foreach (STUInstanceField writtenField in writtenFields) {
                if (!fieldMap.ContainsKey(writtenField.FieldChecksum)) {
                    Debugger.Log(0, "STU", $"[STU:{type}]: Unknown field {writtenField.FieldChecksum:X8} ({writtenField.FieldSize} bytes)\n");
                    if (writtenField.FieldSize == 0) {
                        uint size = reader.ReadUInt32();
                        reader.BaseStream.Position += size;
                        continue;
                    }
                    reader.BaseStream.Position += writtenField.FieldSize;
                    continue;
                }
                FieldInfo field = fieldMap[writtenField.FieldChecksum];
                STUFieldAttribute element = field.GetCustomAttribute<STUFieldAttribute>() ?? new STUFieldAttribute();
                bool skip = false;
                if (element?.STUVersionOnly != null) {
                    skip = element.STUVersionOnly.All(version => version != Version);
                }
                if (element?.IgnoreVersion != null && skip) {
                    skip = !element.IgnoreVersion.Any(stufield => stufield == writtenField.FieldChecksum);
                }
                if (skip) {
                    continue;
                }
                if (!CheckCompatVersion(field, buildVersion)) {
                    continue;
                }
                if (field.FieldType.IsArray) {
                    field.SetValue(instance, InitializeObjectArray(writtenField, field.FieldType.GetElementType(), reader, element));
                }
                else {
                    if (writtenField.FieldSize == 0) {
                        if (field.FieldType.IsClass || field.FieldType.IsValueType) {
                            STUInlineInfo inline = reader.Read<STUInlineInfo>();
                            object inlineInstance = Activator.CreateInstance(field.FieldType);
                            if (inline.FieldListIndex < 0 || inline.FieldListIndex >= instanceFields.Length) {
                                continue;
                            }
                            field.SetValue(instance,
                                InitializeObject(inlineInstance, field.FieldType, instanceFields[inline.FieldListIndex],
                                    reader));
                            continue;
                        }
                    }
                    if (writtenField.FieldSize == 4 && field.FieldType.IsClass && !IsSimple(field.FieldType)) {
                        // this is embedded, don't initialise the class
                        reader.BaseStream.Position += writtenField.FieldSize;
                        continue;
                    }

                    reader.BaseStream.Position += element.Padding;

                    long position = -1;
                    if (element?.ReferenceValue == true) {
                        long offset = reader.ReadInt64();
                        if (offset == 0) {
                            continue;
                        }
                        position = reader.BaseStream.Position;
                        reader.BaseStream.Position = offset;
                    }
                    field.SetValue(instance, GetValue(writtenField, field.FieldType, reader, element));
                    if (position > -1) {
                        reader.BaseStream.Position = position;
                    }
                }
                if (element?.Verify != null) {
                    if (!field.GetValue(instance).Equals(element.Verify)) {
                        throw new Exception("Verification failed");
                    }
                }

                DemangleInstance(instance, writtenField.FieldChecksum);
            }

            return instance;
        }

        protected virtual STUInstance ReadInstance(Type instanceType, STUInstanceField[] writtenFields, BinaryReader reader,
            int size) {
            MemoryStream sandbox = new MemoryStream(size);
            sandbox.Write(reader.ReadBytes(size), 0, size);
            sandbox.Position = 0;
            using (BinaryReader sandboxedReader = new BinaryReader(sandbox, Encoding.UTF8, false)) {
                object instance = Activator.CreateInstance(instanceType);
                return InitializeObject(instance, instanceType, writtenFields, sandboxedReader) as STUInstance;
            }
        }

        private static bool ValidCRC = false;

        protected void GetHeaderCRC() {
            if (!ValidCRC) {
                ValidCRC = CRC64.Hash(0, Encoding.ASCII.GetBytes("123456789")) == 0xe9c6d914c4b8d9ca;
                if (!ValidCRC) {
                    throw new Exception("CRC64 is invalid!");
                }
            }
            byte[] buffer = new byte[36];
            stream.Read(buffer, 0, 36);
            headerCrc = CRC64.Hash(0, buffer);
        }

        private byte[] SwapBytes(byte[] buf, int a, int b) {
            byte a_b = buf[a];
            buf[a] = buf[b];
            buf[b] = a_b;
            return buf;
        }

        private ulong DemangleGUID(ulong guid, uint c32field) {
            ulong checksum = c32field;
            ulong c64field = checksum | (checksum << 32);
            ulong g = guid ^ headerCrc ^ c64field;
            byte[] buf = BitConverter.GetBytes(g);
            buf = SwapBytes(buf, 0, 3);
            buf = SwapBytes(buf, 7, 1);
            buf = SwapBytes(buf, 2, 6);
            buf = SwapBytes(buf, 4, 5);
            return BitConverter.ToUInt64(buf, 0); // ^ ulong.MaxValue;
        }

        // ReSharper disable once PossibleNullReferenceException
        protected virtual void ReadInstanceData(long offset) {
            EmbedArrayRequests = new Dictionary<Array, int[]>();
            stream.Position = offset;
            GetHeaderCRC();
            stream.Position = offset;
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true)) {
                if (instanceTypes == null) {
                    LoadInstanceTypes();
                }

                if (ReadHeaderData(reader)) {
                    if (instanceTypes == null) {
                        LoadInstanceTypes();
                    }

                    long stuOffset = header.Offset;
                    for (int i = 0; i < instanceInfo.Length; ++i) {
                        stream.Position = stuOffset;
                        stuOffset += instanceInfo[i].InstanceSize;

                        instances[i] = null;

                        if (instanceTypes.ContainsKey(instanceInfo[i].InstanceChecksum)) {
                            int fieldListIndex = reader.ReadInt32();
                            if (instanceFields.Length <= fieldListIndex || fieldListIndex < 0) {
                                Debugger.Log(0, "STU", $"[Version2:{instanceInfo[i].InstanceChecksum:X}]: Instance field list was not valid ({fieldListIndex})\n");
                                // todo: why does this happen? Does it just mean that the instance doesn't exist?
                                continue;
                            }
                            instances[i] = ReadInstance(instanceTypes[instanceInfo[i].InstanceChecksum],
                                instanceFields[fieldListIndex], reader, (int) instanceInfo[i].InstanceSize - 4);
                        }
                    }
                    
                    for (int i = 0; i < instanceInfo.Length; ++i) {
                        if (instances[i] != null && 
                            instanceInfo[i].AssignInstanceIndex > -1 &&
                            instanceInfo[i].AssignInstanceIndex < instances.Count) {
                            SetField(instances[instanceInfo[i].AssignInstanceIndex],
                                instanceInfo[i].AssignFieldChecksum, instances[i]);
                            instances[instanceInfo[i].AssignInstanceIndex].Usage = InstanceUsage.Embed;
                        }
                    }
                    foreach (KeyValuePair<Array,int[]> request in EmbedArrayRequests) {
                        int arrayIndex = 0;
                        foreach (int i in request.Value) {
                            if (i < instances.Count) {
                                request.Key.SetValue(instances[i], arrayIndex);
                                if (instances[i] == null) continue;
                                instances[i].Usage = InstanceUsage.EmbedArray;
                            }
                            arrayIndex++;
                        }
                    }
                }
            }
        }

        protected bool ReadHeaderData(BinaryReader reader) {
            header = reader.Read<STUHeader>();
            if (header.InstanceCount == 0 || header.InstanceListOffset == 0) {
                return false;
            }

            instanceInfo = new STUInstanceRecord[header.InstanceCount];
            stream.Position = header.InstanceListOffset;
            for (uint i = 0; i < instanceInfo.Length; ++i) {
                instanceInfo[i] = reader.Read<STUInstanceRecord>();
            }

            instanceArrayRef = new STUInstanceArrayRef[header.EntryInstanceCount];
            stream.Position = header.EntryInstanceListOffset;
            for (uint i = 0; i < instanceArrayRef.Length; ++i) {
                instanceArrayRef[i] = reader.Read<STUInstanceArrayRef>();
            }

            instanceFieldLists = new STUInstanceFieldList[header.InstanceFieldListCount];
            stream.Position = header.InstanceFieldListOffset;
            for (uint i = 0; i < instanceFieldLists.Length; ++i) {
                instanceFieldLists[i] = reader.Read<STUInstanceFieldList>();
            }

            
            instanceFieldLists = new STUInstanceFieldList[header.InstanceFieldListCount];
            stream.Position = header.InstanceFieldListOffset;
            for (uint i = 0; i < instanceFieldLists.Length; ++i) {
                instanceFieldLists[i] = reader.Read<STUInstanceFieldList>();
            }

            instanceFields = new STUInstanceField[header.InstanceFieldListCount][];
            for (uint i = 0; i < instanceFieldLists.Length; ++i) {
                instanceFields[i] = new STUInstanceField[instanceFieldLists[i].FieldCount];
                stream.Position = instanceFieldLists[i].ListOffset;
                for (uint j = 0; j < instanceFields[i].Length; ++j) {
                    instanceFields[i][j] = reader.Read<STUInstanceField>();
                }
            }

            metadata = new MemoryStream((int) header.MetadataSize);
            metadata.Write(reader.ReadBytes((int) header.MetadataSize), 0, (int) header.MetadataSize);
            metadata.Position = 0;
            metadataReader = new BinaryReader(metadata, Encoding.UTF8, false);

            return true;
        }
    }
}
