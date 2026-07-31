using System;
using System.Collections.Generic;
using System.Linq;

namespace ThinkBookToolkit;

public static class AdvancedFanCurve
{
    public static readonly double[] AllowedRates = [0, 10, 20, 50, 100];

    public static List<AdvancedFanCurvePoint> CreateDefaultPoints()
    {
        int[] fan1 = [1500, 1800, 2000, 2400, 2500, 2700, 3100, 3400, 3700, 4100, 4400, 5000];
        int[] fan2 = [1500, 1900, 1900, 1900, 2300, 2700, 3100, 3400, 3700, 4000, 4600, 5000];
        int?[] cpuUp = [46, 52, 58, 64, 70, 76, 79, 82, 85, 88, 92, null];
        int?[] gpuUp = [36, 42, 48, 54, 60, 66, 69, 72, 75, 78, 81, null];
        int?[] cpuDown = [null, 43, 49, 55, 61, 67, 73, 76, 79, 82, 85, 88];
        int?[] gpuDown = [null, 33, 39, 45, 51, 57, 63, 66, 69, 72, 75, 78];
        var result = new List<AdvancedFanCurvePoint>(fan1.Length);
        for (var index = 0; index < fan1.Length; index++)
        {
            result.Add(new AdvancedFanCurvePoint
            {
                Fan1Rpm = fan1[index],
                Fan2Rpm = fan2[index],
                CpuRampUpTemperatureC = cpuUp[index],
                CpuRampDownTemperatureC = cpuDown[index],
                GpuRampUpTemperatureC = gpuUp[index],
                GpuRampDownTemperatureC = gpuDown[index],
                RampUpRpmPerSecond = index < 4 ? 50 : index < 9 ? 100 : 0,
                RampDownRpmPerSecond = index < 4 ? 20 : index < 9 ? 50 : 100
            });
        }
        return result;
    }

    public static AdvancedFanCurveSettings Clone(AdvancedFanCurveSettings value) => new()
    {
        TemperatureSmoothing = value.TemperatureSmoothing,
        Points = value.Points.Select(Clone).ToList()
    };

    public static AdvancedFanCurvePoint Clone(AdvancedFanCurvePoint value) => new()
    {
        Fan1Rpm = value.Fan1Rpm,
        Fan2Rpm = value.Fan2Rpm,
        CpuRampUpTemperatureC = value.CpuRampUpTemperatureC,
        CpuRampDownTemperatureC = value.CpuRampDownTemperatureC,
        GpuRampUpTemperatureC = value.GpuRampUpTemperatureC,
        GpuRampDownTemperatureC = value.GpuRampDownTemperatureC,
        RampUpRpmPerSecond = value.RampUpRpmPerSecond,
        RampDownRpmPerSecond = value.RampDownRpmPerSecond
    };

    public static bool TryValidate(
        IReadOnlyList<AdvancedFanCurvePoint>? points,
        out string error)
    {
        error = string.Empty;
        if (points is null || points.Count < 2)
        {
            error = "Advanced curve requires at least two points.";
            return false;
        }

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (!IsHundredRpm(point.Fan1Rpm) || !IsHundredRpm(point.Fan2Rpm))
            {
                error = $"Point {index + 1} fan speeds must be multiples of 100 RPM.";
                return false;
            }
            if (!IsAllowedRate(point.RampUpRpmPerSecond) ||
                !IsAllowedRate(point.RampDownRpmPerSecond))
            {
                error = $"Point {index + 1} has an unsupported acceleration or deceleration rate.";
                return false;
            }

            var first = index == 0;
            var last = index == points.Count - 1;
            if (first &&
                (point.CpuRampDownTemperatureC is not null ||
                 point.GpuRampDownTemperatureC is not null))
            {
                error = "The lowest-temperature point must not have ramp-down thresholds.";
                return false;
            }
            if (last &&
                (point.CpuRampUpTemperatureC is not null ||
                 point.GpuRampUpTemperatureC is not null))
            {
                error = "The highest-temperature point must not have ramp-up thresholds.";
                return false;
            }
            if (!first &&
                (point.CpuRampDownTemperatureC is null ||
                 point.GpuRampDownTemperatureC is null) ||
                !last &&
                (point.CpuRampUpTemperatureC is null ||
                 point.GpuRampUpTemperatureC is null))
            {
                error = $"Point {index + 1} is missing a required temperature threshold.";
                return false;
            }
            if (!TemperaturesAreValid(point))
            {
                error = $"Point {index + 1} contains a temperature outside 0–127 °C.";
                return false;
            }
            if (index > 0)
            {
                var previous = points[index - 1];
                if (point.Fan1Rpm < previous.Fan1Rpm ||
                    point.Fan2Rpm < previous.Fan2Rpm ||
                    !NonDecreasing(previous.CpuRampUpTemperatureC, point.CpuRampUpTemperatureC) ||
                    !NonDecreasing(previous.GpuRampUpTemperatureC, point.GpuRampUpTemperatureC) ||
                    !NonDecreasing(previous.CpuRampDownTemperatureC, point.CpuRampDownTemperatureC) ||
                    !NonDecreasing(previous.GpuRampDownTemperatureC, point.GpuRampDownTemperatureC))
                {
                    error = "Fan speeds and temperature thresholds must not decrease from left to right.";
                    return false;
                }
            }
        }
        return true;
    }

    public static AdvancedFanCurveSettings Normalize(
        AdvancedFanCurveSettings? value,
        FanRpmLimits limits)
    {
        var fallback = new AdvancedFanCurveSettings();
        if (value is null || !TryValidate(value.Points, out _))
            value = fallback;

        var result = Clone(value);
        result.TemperatureSmoothing = NormalizeSmoothing(
            result.TemperatureSmoothing);
        foreach (var point in result.Points)
        {
            point.Fan1Rpm = ClampRpm(
                point.Fan1Rpm,
                limits.Fan1MinimumRpm,
                limits.Fan1MaximumRpm);
            point.Fan2Rpm = ClampRpm(
                point.Fan2Rpm,
                limits.Fan2MinimumRpm,
                limits.Fan2MaximumRpm);
        }
        return TryValidate(result.Points, out _) ? result : fallback;
    }

    public static (int Index, FanTargets Target, double RampUp, double RampDown) Evaluate(
        IReadOnlyList<AdvancedFanCurvePoint> points,
        int currentIndex,
        double? cpuTemperatureC,
        double? gpuTemperatureC)
    {
        currentIndex = Math.Clamp(currentIndex, 0, points.Count - 1);
        while (currentIndex < points.Count - 1)
        {
            var current = points[currentIndex];
            if (!AtOrAbove(cpuTemperatureC, current.CpuRampUpTemperatureC) &&
                !AtOrAbove(gpuTemperatureC, current.GpuRampUpTemperatureC))
            {
                break;
            }
            currentIndex++;
        }
        while (currentIndex > 0)
        {
            var current = points[currentIndex];
            if (!HasTemperature(cpuTemperatureC, gpuTemperatureC) ||
                !AtOrBelow(cpuTemperatureC, current.CpuRampDownTemperatureC) ||
                !AtOrBelow(gpuTemperatureC, current.GpuRampDownTemperatureC))
            {
                break;
            }
            currentIndex--;
        }

        var selected = points[currentIndex];
        return (
            currentIndex,
            new FanTargets(selected.Fan1Rpm, selected.Fan2Rpm),
            selected.RampUpRpmPerSecond,
            selected.RampDownRpmPerSecond);
    }

    private static bool HasTemperature(double? cpu, double? gpu) =>
        cpu.HasValue || gpu.HasValue;

    private static bool AtOrAbove(double? value, int? threshold) =>
        value.HasValue && threshold.HasValue && value.Value >= threshold.Value;

    private static bool AtOrBelow(double? value, int? threshold) =>
        !value.HasValue || threshold.HasValue && value.Value <= threshold.Value;

    private static bool IsHundredRpm(int value) =>
        value is >= CurveProfileStore.AbsoluteMinimumFanRpm and
            <= CurveProfileStore.AbsoluteMaximumFanRpm &&
        value % 100 == 0;

    private static bool IsAllowedRate(double value) =>
        double.IsFinite(value) && AllowedRates.Contains(value);

    private static bool TemperaturesAreValid(AdvancedFanCurvePoint point) =>
        new int?[]
        {
            point.CpuRampUpTemperatureC,
            point.CpuRampDownTemperatureC,
            point.GpuRampUpTemperatureC,
            point.GpuRampDownTemperatureC
        }.All(value => value is null or >= 0 and <= 127);

    private static bool NonDecreasing(int? previous, int? current) =>
        previous is null || current is null || current >= previous;

    private static int ClampRpm(int value, int minimum, int maximum) =>
        CurveProfileStore.ClampRpm(value, minimum, maximum);

    private static double NormalizeSmoothing(double value)
    {
        if (!double.IsFinite(value))
            return 3;
        return new[] { 1d, 2d, 3d, 5d, 10d }
            .OrderBy(candidate => Math.Abs(candidate - value))
            .First();
    }
}

public sealed class FanRateLimiter
{
    private double? _fan1;
    private double? _fan2;
    private DateTimeOffset? _lastUpdate;

    public FanTargets Apply(
        FanTargets target,
        double rampUpRpmPerSecond,
        double rampDownRpmPerSecond,
        DateTimeOffset now,
        bool limitChanges)
    {
        if (!limitChanges || _fan1 is null || _fan2 is null || _lastUpdate is null)
        {
            _fan1 = target.Fan1Rpm;
            _fan2 = target.Fan2Rpm;
            _lastUpdate = now;
            return Round(target);
        }

        var elapsed = Math.Max(0, (now - _lastUpdate.Value).TotalSeconds);
        _lastUpdate = now;
        _fan1 = Move(_fan1.Value, target.Fan1Rpm, rampUpRpmPerSecond, rampDownRpmPerSecond, elapsed);
        _fan2 = Move(_fan2.Value, target.Fan2Rpm, rampUpRpmPerSecond, rampDownRpmPerSecond, elapsed);
        return new FanTargets(
            RoundTowardTarget(_fan1.Value, target.Fan1Rpm),
            RoundTowardTarget(_fan2.Value, target.Fan2Rpm));
    }

    public void Reset()
    {
        _fan1 = null;
        _fan2 = null;
        _lastUpdate = null;
    }

    private static double Move(
        double current,
        int target,
        double upRate,
        double downRate,
        double seconds)
    {
        if (target == 0 || current == 0)
            return target;
        if (target > current && upRate > 0)
            return Math.Min(target, current + upRate * seconds);
        if (target < current && downRate > 0)
            return Math.Max(target, current - downRate * seconds);
        return target;
    }

    private static int RoundTowardTarget(double value, int target)
    {
        if (value < target)
            return (int)Math.Floor(value / 100) * 100;
        if (value > target)
            return (int)Math.Ceiling(value / 100) * 100;
        return CurveProfileStore.SnapRpm(value);
    }

    private static FanTargets Round(FanTargets value) => new(
        CurveProfileStore.SnapRpm(value.Fan1Rpm),
        CurveProfileStore.SnapRpm(value.Fan2Rpm));
}
