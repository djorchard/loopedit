using System.Text;

namespace OrchardSound;

public sealed record CuePoint(long Frame, string Name);

public static class WavCueFile
{
    const int MaximumCues = 4096;
    public static CuePoint[] Read(string path)
    {
        if (!Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) return [];
        try
        {
            using var file = File.OpenRead(path); using var reader = new BinaryReader(file, Encoding.UTF8, true);
            string Four() => Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (file.Length < 12 || Four() != "RIFF") return [];
            _ = reader.ReadUInt32(); if (Four() != "WAVE") return [];
            var points = new List<(uint Id, uint Frame)>(); var labels = new Dictionary<uint, string>(); uint rate = AudioTrack.Rate;
            while (file.Position <= file.Length - 8)
            {
                string kind = Four(); uint size = reader.ReadUInt32(); long end = file.Position + size;
                if (end > file.Length) break;
                if (kind == "fmt " && size >= 16) { _ = reader.ReadUInt16(); _ = reader.ReadUInt16(); rate = reader.ReadUInt32(); }
                if (kind == "cue " && size >= 4)
                {
                    uint count = reader.ReadUInt32();
                    if (count > MaximumCues || 4L + count * 24L > size) return [];
                    for (int i = 0; i < count; i++)
                    {
                        uint id = reader.ReadUInt32(); _ = reader.ReadUInt32(); string chunk = Four();
                        uint chunkStart = reader.ReadUInt32(), blockStart = reader.ReadUInt32(), frame = reader.ReadUInt32();
                        if (chunk == "data" && chunkStart == 0 && blockStart == 0) points.Add((id, frame));
                    }
                }
                if (kind == "LIST" && size >= 4 && Four() == "adtl")
                    while (file.Position <= end - 8)
                    {
                        string sub = Four(); uint length = reader.ReadUInt32(); long subEnd = file.Position + length;
                        if (subEnd > end) break;
                        if (sub == "labl" && length is >= 4 and <= 65536)
                        {
                            uint id = reader.ReadUInt32(); string name = Encoding.UTF8.GetString(reader.ReadBytes((int)length - 4)).TrimEnd('\0');
                            labels[id] = new string(name.Where(c => !char.IsControl(c)).Take(110).ToArray());
                        }
                        file.Position = Math.Min(end, subEnd + (length & 1));
                    }
                file.Position = Math.Min(file.Length, end + (size & 1));
            }
            if (rate == 0) return [];
            return points.Select(p => new CuePoint((long)Math.Round(p.Frame * (double)AudioTrack.Rate / rate), labels.GetValueOrDefault(p.Id, "Cue " + p.Id)))
                .OrderBy(p => p.Frame).DistinctBy(p => p.Frame).ToArray();
        }
        catch (IOException) { return []; }
        catch (OverflowException) { return []; }
    }

    public static void Append(string path, IEnumerable<CuePoint> cues)
    {
        var points = cues.Where(c => c.Frame >= 0 && c.Frame <= uint.MaxValue).OrderBy(c => c.Frame).DistinctBy(c => c.Frame).Take(MaximumCues).ToArray();
        if (points.Length == 0) return;
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var writer = new BinaryWriter(file, Encoding.UTF8, true);
        void Four(string value) => writer.Write(Encoding.ASCII.GetBytes(value));
        if (file.Length > uint.MaxValue - 1_000_000) throw new IOException("WAV cue data exceeds the RIFF size limit.");
        file.Position = file.Length; if ((file.Position & 1) != 0) writer.Write((byte)0);
        Four("cue "); writer.Write((uint)(4 + points.Length * 24)); writer.Write((uint)points.Length);
        for (int i = 0; i < points.Length; i++)
        { writer.Write((uint)i + 1); writer.Write((uint)points[i].Frame); Four("data"); writer.Write(0u); writer.Write(0u); writer.Write((uint)points[i].Frame); }
        using var list = new MemoryStream();
        using (var labels = new BinaryWriter(list, Encoding.UTF8, true))
        {
            labels.Write(Encoding.ASCII.GetBytes("adtl"));
            for (int i = 0; i < points.Length; i++)
            {
                byte[] name = Encoding.UTF8.GetBytes(new string(points[i].Name.Where(c => !char.IsControl(c)).Take(110).ToArray()) + "\0");
                labels.Write(Encoding.ASCII.GetBytes("labl")); labels.Write((uint)(4 + name.Length)); labels.Write((uint)i + 1); labels.Write(name);
                if ((name.Length & 1) != 0) labels.Write((byte)0);
            }
        }
        Four("LIST"); writer.Write((uint)list.Length); writer.Write(list.ToArray());
        file.Position = 4; writer.Write(checked((uint)(file.Length - 8)));
    }
}
