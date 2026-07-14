using BabyMonitor.Core.Data;

namespace BabyMonitor.Core.Monitor;

/// <summary>ALRM-7: when the noise alarm is armed. Pure — time injected as minutes-of-day.</summary>
public sealed record AlarmSchedule(bool Windowed, int StartMinutes = 0, int EndMinutes = 0)
{
    /// <summary>Is the alarm armed at <paramref name="minutesOfDay"/> (0..1439)? Windows may cross midnight.</summary>
    public bool IsActive(int minutesOfDay)
    {
        if (!Windowed)
        {
            return true;
        }

        if (StartMinutes == EndMinutes)
        {
            return true; // degenerate window = always
        }

        return StartMinutes < EndMinutes
            ? minutesOfDay >= StartMinutes && minutesOfDay < EndMinutes
            : minutesOfDay >= StartMinutes || minutesOfDay < EndMinutes; // crosses midnight
    }

    public static AlarmSchedule From(Settings s) => new(
        Windowed: s.AlarmScheduleMode == Settings.ScheduleWindow,
        StartMinutes: s.AlarmWindowStartMinutes,
        EndMinutes: s.AlarmWindowEndMinutes);
}
