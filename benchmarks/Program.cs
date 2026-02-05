using BenchmarkDotNet.Running;
using MfsReader.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
