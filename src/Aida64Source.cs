using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsMonitor
{
    /// <summary>
    /// 通过 AIDA64 的"外部应用 → 共享内存"接口读取 CPU 传感器数据。
    /// 数据由 AIDA64（其通过 HVCI 认证的内核驱动）采集，本类只是读取方，不需要任何内核驱动，
    /// 因此在开启"内存完整性"时同样可用。
    /// 需要在 AIDA64 中启用：文件 → 设置 → 硬件监控 → 外部应用 → 启用共享内存。
    /// </summary>
    public class Aida64Source
    {
        private const string MapName = "AIDA64_SensorValues";
        private const int MaxLength = 262144;
        private const uint FileMapRead = 0x0004;

        /// <summary>共享内存当前是否可访问（AIDA64 是否在运行并启用了共享内存）。</summary>
        public bool IsAvailable
        {
            get
            {
                IntPtr h = OpenFileMapping(FileMapRead, false, MapName);
                if (h == IntPtr.Zero) return false;
                CloseHandle(h);
                return true;
            }
        }

        /// <summary>
        /// 读取一次 CPU 温度(°C)/功耗(W)/频率(MHz)。AIDA64 未运行或未启用共享内存时返回 false。
        /// </summary>
        public bool TryRead(out float? cpuTemp, out float? cpuPower, out float? cpuClock)
        {
            cpuTemp = null;
            cpuPower = null;
            cpuClock = null;

            string raw = ReadRaw();
            if (raw == null) return false;

            Dictionary<string, string> values = Parse(raw);
            if (values.Count == 0) return false;

            float v;

            // 温度：优先 CPU Diode（AMD 上是封装温度），其次 CCD1
            if (TryFloat(values, "TCPUDIO", out v)) cpuTemp = v;
            else if (TryFloat(values, "TCCD1", out v)) cpuTemp = v;

            // 功耗：CPU Package
            if (TryFloat(values, "PCPUPKG", out v)) cpuPower = v;

            // 频率：优先 AIDA64 的 CPU Clock，缺省时取所有核心时钟的平均值
            if (TryFloat(values, "SCPUCLK", out v)) cpuClock = v;
            else
            {
                double sum = 0;
                int count = 0;
                foreach (KeyValuePair<string, string> kv in values)
                {
                    if (kv.Key.StartsWith("SCC-", StringComparison.Ordinal))
                    {
                        float c;
                        if (float.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out c))
                        {
                            sum += c;
                            count++;
                        }
                    }
                }
                if (count > 0) cpuClock = (float)(sum / count);
            }

            return cpuTemp.HasValue || cpuPower.HasValue || cpuClock.HasValue;
        }

        /// <summary>按传感器 ID 读取原始字符串值（如 GPU 降频原因 SGPU1PERFCAP）；不可用时返回 null。</summary>
        public string GetValue(string id)
        {
            string raw = ReadRaw();
            if (raw == null) return null;
            Dictionary<string, string> map = Parse(raw);
            string v;
            return map.TryGetValue(id, out v) ? v : null;
        }

        private string ReadRaw()
        {
            IntPtr h = OpenFileMapping(FileMapRead, false, MapName);
            if (h == IntPtr.Zero) return null;
            try
            {
                IntPtr p = MapViewOfFile(h, FileMapRead, 0, 0, UIntPtr.Zero);
                if (p == IntPtr.Zero) return null;
                try
                {
                    StringBuilder sb = new StringBuilder(MaxLength);
                    for (int i = 0; i < MaxLength; i++)
                    {
                        byte b = Marshal.ReadByte(p, i);
                        if (b == 0) break;
                        sb.Append((char)b);
                    }
                    return sb.ToString();
                }
                finally
                {
                    UnmapViewOfFile(p);
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>
        /// 解析共享内存中的 XML 风格记录：&lt;sys&gt;&lt;id&gt;ID&lt;/id&gt;&lt;label&gt;..&lt;/label&gt;&lt;value&gt;V&lt;/value&gt;&lt;/sys&gt;
        /// 逐段顺序扫描 id/value，对包含特殊字符的值也安全。
        /// </summary>
        private static Dictionary<string, string> Parse(string s)
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            int pos = 0;
            while (true)
            {
                int idStart = s.IndexOf("<id>", pos, StringComparison.Ordinal);
                if (idStart < 0) break;
                int idEnd = s.IndexOf("</id>", idStart + 4, StringComparison.Ordinal);
                if (idEnd < 0) break;
                string id = s.Substring(idStart + 4, idEnd - idStart - 4);

                int valStart = s.IndexOf("<value>", idEnd + 5, StringComparison.Ordinal);
                if (valStart < 0) break;
                valStart += 7;
                int valEnd = s.IndexOf("</value>", valStart, StringComparison.Ordinal);
                if (valEnd < 0) break;
                string val = s.Substring(valStart, valEnd - valStart);

                if (!map.ContainsKey(id)) map[id] = val;
                pos = valEnd + 8;
            }
            return map;
        }

        private static bool TryFloat(Dictionary<string, string> map, string id, out float v)
        {
            v = 0f;
            string s;
            if (!map.TryGetValue(id, out s)) return false;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenFileMapping(uint dwDesiredAccess, bool bInheritHandle, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
            uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
