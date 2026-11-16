namespace Ryujinx.Cpu.LightningJit
{
    public static class CpuPresets
    {
        public static CpuPreset CortexA57 => new(
            IsaVersion.v80,
            IsaFeature.FeatAes |
            IsaFeature.FeatCrc32 |
            IsaFeature.FeatSha1 |
            IsaFeature.FeatSha256 |
            IsaFeature.FeatPmull);

        // The Switch 2 CPU: ARMv8.2 with the LSE atomics, RCPC, pointer authentication, dot product,
        // half-precision and flag manipulation extensions. Version gates instructions that have no
        // feature of their own in the table (the atomics are v8.1, LDAPR v8.3, LDAPUR v8.4).
        public static CpuPreset CortexA78C => new(
            IsaVersion.v84,
            IsaFeature.FeatAes |
            IsaFeature.FeatCrc32 |
            IsaFeature.FeatSha1 |
            IsaFeature.FeatSha256 |
            IsaFeature.FeatSha3 |
            IsaFeature.FeatSha512 |
            IsaFeature.FeatPmull |
            IsaFeature.FeatLse |
            IsaFeature.FeatRdm |
            IsaFeature.FeatFp16 |
            IsaFeature.FeatDotprod |
            IsaFeature.FeatLrcpc |
            IsaFeature.FeatLrcpc2 |
            IsaFeature.FeatPauth |
            IsaFeature.FeatFlagm |
            IsaFeature.FeatRas);

        /// <summary>
        /// Preset the translator compiles guest code for. Set before the guest starts running.
        /// </summary>
        public static CpuPreset Current { get; set; } = CortexA57;
    }
}
