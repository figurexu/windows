using System;
using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;

namespace WindowsMonitor
{
    /// <summary>一次采样得到的 CPU / GPU 传感器快照。</summary>
    public class HardwareSnapshot
    {
        public string CpuName = "";
        public string GpuName = "";
        public bool CpuBlocked;
        public bool CpuFromAida64;
        public float? CpuTemperature;
        public float? CpuPower;
        public float? CpuClock;
        public float? GpuTemperature;
        public float? GpuPower;
        public float? GpuClock;
        public string GpuPerfCapReason = "";
    }

    /// <summary>
    /// 基于 LibreHardwareMonitor 的传感器读取服务。
    /// 说明：CPU 温度/功耗需要读取 MSR（需管理员权限，应用清单已声明）。
    /// </summary>
    public class MonitorService : IDisposable
    {
        private readonly Computer _computer;
        private readonly UpdateVisitor _visitor = new UpdateVisitor();
        private readonly Aida64Source _aida64 = new Aida64Source();
        private readonly NativeCpuSource _nativeCpu = new NativeCpuSource();
        private bool _disposed;

        public MonitorService()
        {
            _computer = new Computer();
            _computer.IsCpuEnabled = true;
            _computer.IsGpuEnabled = true;
            _computer.IsMemoryEnabled = false;
            _computer.IsMotherboardEnabled = false;
            _computer.IsControllerEnabled = false;
            _computer.IsNetworkEnabled = false;
            _computer.IsStorageEnabled = false;
            _computer.Open();
        }

        /// <summary>刷新并读取全部需要显示的传感器值。</summary>
        public HardwareSnapshot ReadSnapshot()
        {
            _computer.Accept(_visitor);

            HardwareSnapshot snap = new HardwareSnapshot();

            IHardware cpu = FindFirst(HardwareType.Cpu);
            if (cpu != null) snap.CpuName = cpu.Name;

            // CPU 优先走 AIDA64 共享内存（HVCI 兼容，无需内核驱动）；
            // 不可用时降级到 Windows 原生性能计数器（仅频率；温度/功耗需内核驱动，保持 null）。
            float? aidaTemp, aidaPower, aidaClock;
            if (_aida64.TryRead(out aidaTemp, out aidaPower, out aidaClock))
            {
                snap.CpuFromAida64 = true;
                snap.CpuTemperature = aidaTemp;
                snap.CpuPower = aidaPower;
                snap.CpuClock = aidaClock;
                if (snap.CpuName.Length == 0) snap.CpuName = "CPU";
            }
            else
            {
                snap.CpuFromAida64 = false;
                snap.CpuClock = _nativeCpu.ReadClock();
                snap.CpuPower = _nativeCpu.ReadPower(); // 原生无功耗读数
                snap.CpuTemperature = null;            // 原生无温度读数
                if (cpu != null && snap.CpuName.Length == 0) snap.CpuName = cpu.Name;
                if (snap.CpuName.Length == 0) snap.CpuName = "CPU";
            }

            IHardware gpu = FindFirst(HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel);
            if (gpu != null)
            {
                snap.GpuName = gpu.Name;
                snap.GpuTemperature = PickSensor(gpu, SensorType.Temperature,
                    new string[] { "GPU Core", "GPU", "Core" });
                snap.GpuPower = PickSensor(gpu, SensorType.Power,
                    new string[] { "GPU Power", "GPU Core", "GPU Package", "GPU Total", "GPU" });
                snap.GpuClock = PickSensor(gpu, SensorType.Clock,
                    new string[] { "GPU Core", "GPU" });
            }

            // GPU 降频原因来自 AIDA64（NVAPI 的 PerfCap 汇总），用于判断是否受散热/功耗限制
            try
            {
                string reason = _aida64.GetValue("SGPU1PERFCAP");
                if (!string.IsNullOrEmpty(reason)) snap.GpuPerfCapReason = reason;
            }
            catch { }

            return snap;
        }

        private IHardware FindFirst(params HardwareType[] types)
        {
            foreach (IHardware h in _computer.Hardware)
            {
                foreach (HardwareType t in types)
                {
                    if (h.HardwareType == t) return h;
                }
            }
            return null;
        }

        /// <summary>
        /// 按优先级在名称中匹配传感器；全部不匹配时退回最大值（如最热核心）。
        /// </summary>
        private static float? PickSensor(IHardware hardware, SensorType type, string[] preferredNames)
        {
            List<ISensor> sensors = new List<ISensor>();
            foreach (ISensor s in hardware.Sensors)
            {
                if (s.SensorType == type && s.Value.HasValue) sensors.Add(s);
            }
            if (sensors.Count == 0) return null;

            foreach (string name in preferredNames)
            {
                foreach (ISensor s in sensors)
                {
                    if (s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                        return s.Value;
                }
            }

            float best = float.MinValue;
            foreach (ISensor s in sensors)
            {
                if (s.Value.Value > best) best = s.Value.Value;
            }
            return best;
        }

        /// <summary>CPU 频率：优先取 “Average”，否则取所有核心时钟的平均值。</summary>
        private static float? PickClock(IHardware cpu)
        {
            List<ISensor> clocks = new List<ISensor>();
            foreach (ISensor s in cpu.Sensors)
            {
                if (s.SensorType == SensorType.Clock && s.Value.HasValue) clocks.Add(s);
            }
            if (clocks.Count == 0) return null;

            foreach (ISensor s in clocks)
            {
                if (s.Name.IndexOf("Average", StringComparison.OrdinalIgnoreCase) >= 0)
                    return s.Value;
            }

            double sum = 0.0;
            foreach (ISensor s in clocks) sum += s.Value.Value;
            return (float)(sum / clocks.Count);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _computer.Close(); }
            catch { /* 忽略关闭异常 */ }
            try { _nativeCpu.Dispose(); }
            catch { }
        }

        /// <summary>遍历所有已启用的硬件并刷新其传感器。</summary>
        private sealed class UpdateVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer)
            {
                computer.Traverse(this);
            }

            public void VisitHardware(IHardware hardware)
            {
                hardware.Update();
                foreach (IHardware sub in hardware.SubHardware)
                {
                    sub.Accept(this);
                }
            }

            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }
    }
}
