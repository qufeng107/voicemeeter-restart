using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;

namespace Reconnect
{
    [DataContract]
    public sealed class WireStatus
    {
        [DataMember] public Snapshot Snapshot;
        [DataMember] public string[] Lines;
        [DataMember] public bool Enabled;
        [DataMember] public string Error;
        [DataMember] public string DataDirectory;
    }
    public static class DaemonClient
    {
        public static string PipeName { get { return "VoicemeeterReconnect." + WindowsIdentity.GetCurrent().User.Value; } }
        public static WireStatus Send(string command)
        {
            using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None))
            {
                pipe.Connect(1500);
                using (var writer = new BinaryWriter(pipe, System.Text.Encoding.UTF8, true)) { writer.Write(command); writer.Flush(); }
                using (var reader = new BinaryReader(pipe, System.Text.Encoding.UTF8, true))
                {
                    int length = reader.ReadInt32();
                    if (length < 0 || length > 1024 * 1024) throw new InvalidDataException("后台响应异常。");
                    byte[] bytes = reader.ReadBytes(length);
                    if (bytes.Length != length) throw new EndOfStreamException();
                    using (var stream = new MemoryStream(bytes)) return (WireStatus)new DataContractJsonSerializer(typeof(WireStatus)).ReadObject(stream);
                }
            }
        }
        public static void Reply(Stream pipe, WireStatus status)
        {
            using (var memory = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(WireStatus)).WriteObject(memory, status);
                byte[] bytes = memory.ToArray();
                using (var writer = new BinaryWriter(pipe, System.Text.Encoding.UTF8, true)) { writer.Write(bytes.Length); writer.Write(bytes); writer.Flush(); }
            }
        }
    }
}
