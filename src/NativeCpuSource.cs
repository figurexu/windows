using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace WindowsMonitor
{
    /// <summary>
    /// 无依赖 CPU 数据源（Windows 原生性能计数器）。
    /// 频率 = % Processor Performance × 基准频率（TSC）。
    /// 温度/功耗在用户态没有原生途径（读 MSR 能量/温度寄存器需要内核驱动，
    /// 即 AIDA64 / HWiNFO 的职责），因此返回 null，由数据源恢复后自动补齐。
    /// </summary>
    public class NativeCpuSource : IDisposable
    {
        private PerformanceCounter _perfPerf;
        private float _baseMhz;
        private bool _initOk;

        public NativeCpuSource()
        {
            try
            {
                _baseMhz = ReadBaseMhz();
                if (_baseMhz <= 0f) return;
                if (PerformanceCounterCategory.Exists("Processor Information"))
                {
                    _perfPerf = new PerformanceCounter(
                        "Processor Information", "% Processor Performance", "_Total", true);
                    _initOk = true;
                }
            }
            catch { _initOk = false; }
        }

        private static float ReadBaseMhz()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k == null) return 0f;
                    object v = k.GetValue("~MHz");
                    return v == null ? 0f : Convert.ToSingle(v);
                }
            }
            catch { return 0f; }
        }

        /// <summary>当前 CPU 频率（MHz）。不可用（无计数器/首帧）时返回 null。</summary>
        public float? ReadClock()
        {
            if (!_initOk || _perfPerf == null) return null;
            try
            {
                float pct = _perfPerf.NextValue();
                // 首帧无基准返回 0；数值异常时忽略
                if (pct <= 0f || float.IsNaN(pct)) return null;
                return _baseMhz * pct / 100f;
            }
            catch { return null; }
        }

        /// <summary>CPU 功耗：原生用户态无读数（Windows 不暴露能量计数器），恒为 null。</summary>
        public float? ReadPower() { return null; }

        public void Dispose()
        {
            if (_perfPerf != null)
            {
                try { _perfPerf.Dispose(); } catch { }
                _perfPerf = null;
            }
        }
    }
}
