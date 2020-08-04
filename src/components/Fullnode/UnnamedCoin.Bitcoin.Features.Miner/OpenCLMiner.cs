using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cloo;
using Microsoft.Extensions.Logging;

namespace UnnamedCoin.Bitcoin.Features.Miner
{
    public class OpenCLMiner : IDisposable
    {
        private static readonly string KernelCodeH1 = File.ReadAllText(@$"{AppContext.BaseDirectory}OpenCL{Path.DirectorySeparatorChar}opencl_device_info.h");
        private static readonly string KernelCodeH2 = File.ReadAllText(@$"{AppContext.BaseDirectory}OpenCL{Path.DirectorySeparatorChar}opencl_misc.h");
        private static readonly string KernelCodeH3 = File.ReadAllText(@$"{AppContext.BaseDirectory}OpenCL{Path.DirectorySeparatorChar}opencl_sha2_common.h");
        private static readonly string KernelCodeH4 = File.ReadAllText(@$"{AppContext.BaseDirectory}OpenCL{Path.DirectorySeparatorChar}opencl_sha512.h");
        private static readonly string KernelCodeMain = File.ReadAllText(@$"{AppContext.BaseDirectory}OpenCL{Path.DirectorySeparatorChar}sha512_miner.cl");
        private const string KernelFunction = "kernel_find_pow";

        private readonly ILogger logger;
        private readonly ComputeDevice computeDevice;

        private List<ComputeKernel> computeKernels = new List<ComputeKernel>();
        private ComputeProgram computeProgram;
        private ComputeContext computeContext;
        private ComputeKernel computeKernel;

        public OpenCLMiner(MinerSettings minerSettings, ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
            var devices = ComputePlatform.Platforms.SelectMany(p => p.Devices).Where(d => d.Available && d.CompilerAvailable).ToList();

            if (!devices.Any())
            {
                this.logger.LogWarning($"No OpenCL Devices Found!");
            }
            else
            {
                foreach (var device in devices)
                {
                    this.logger.LogInformation($"Found OpenCL Device: Name={device.Name}, MaxClockFrequency{device.MaxClockFrequency}");
                }

                this.computeDevice = devices.FirstOrDefault(d => d.Name.Equals(minerSettings.OpenCLDevice, StringComparison.OrdinalIgnoreCase)) ?? devices.FirstOrDefault();
                if (this.computeDevice != null)
                {
                    this.logger.LogInformation($"Using OpenCL Device: Name={this.computeDevice.Name}");
                }
            }
        }

        ~OpenCLMiner()
        {
            this.DisposeOpenCLResources();
        }

        public bool CanMine()
        {
            return this.computeDevice != null;
        }

        public string GetDeviceName()
        {
            if (this.computeDevice == null)
            {
                throw new InvalidOperationException("GPU not found");
            }

            return this.computeDevice.Name;
        }

        public uint FindPow(byte[] header, byte[] bits, uint nonceStart, uint iterations)
        {
            if (this.computeDevice == null)
            {
                throw new InvalidOperationException("GPU not found");
            }

            this.ConstructOpenCLResources();

            using var headerBuffer = new ComputeBuffer<byte>(this.computeContext, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, header);
            using var bitsBuffer = new ComputeBuffer<byte>(this.computeContext, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, bits);
            using var powBuffer = new ComputeBuffer<uint>(this.computeContext, ComputeMemoryFlags.WriteOnly, 1);

            this.computeKernel.SetMemoryArgument(0, headerBuffer);
            this.computeKernel.SetMemoryArgument(1, bitsBuffer);
            this.computeKernel.SetValueArgument(2, nonceStart);
            this.computeKernel.SetMemoryArgument(3, powBuffer);

            using var commands = new ComputeCommandQueue(this.computeContext, this.computeDevice, ComputeCommandQueueFlags.None);
            commands.Execute(this.computeKernel, null, new long[] { iterations }, null, null);

            var nonceOut = new uint[1];
            commands.ReadFromBuffer(powBuffer, ref nonceOut, true, null);
            commands.Finish();

            this.DisposeOpenCLResources();

            return nonceOut[0];
        }

        private void ConstructOpenCLResources()
        {
            if (this.computeDevice != null)
            {
                var properties = new ComputeContextPropertyList(this.computeDevice.Platform);
                this.computeContext = new ComputeContext(new[] { this.computeDevice }, properties, null, IntPtr.Zero);
                this.computeProgram = new ComputeProgram(this.computeContext, new string[] { KernelCodeH1, KernelCodeH2, KernelCodeH3, KernelCodeH4, KernelCodeMain });
                this.computeProgram.Build(new[] { this.computeDevice }, null, null, IntPtr.Zero);
                this.computeKernels = this.computeProgram.CreateAllKernels().ToList();
                this.computeKernel = this.computeKernels.First((k) => k.FunctionName == KernelFunction);
            }
        }

        private void DisposeOpenCLResources()
        {
            this.computeKernels.ForEach(k => k.Dispose());
            this.computeKernels.Clear();
            this.computeProgram?.Dispose();
            this.computeProgram = null;
            this.computeContext?.Dispose();
            this.computeContext = null;
        }

        public void Dispose()
        {
            this.DisposeOpenCLResources();
            GC.SuppressFinalize(this);
        }
    }
}
