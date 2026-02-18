using System;
using System.Collections.Generic;

namespace CheerfulGiverNXT;

public enum PledgeFrequency
{
    Weekly,
    EveryTwoWeeks,
    EveryFourWeeks,
    Monthly,
    Quarterly,
    Annually
}

public sealed record PledgeInstallment(decimal Amount, DateTime Date, int Sequence);

public static class PledgeScheduleBuilder
{
    public static IReadOnlyList<PledgeInstallment> Build(
        decimal totalAmount,
        DateTime firstInstallmentDateLocal,
        PledgeFrequency frequency,
        int numberOfInstallments)
    {
        if (numberOfInstallments <= 0)
            throw new ArgumentOutOfRangeException(nameof(numberOfInstallments));

        // Split to cents so the sum ALWAYS equals the pledge amount.
        var totalCents = (int)Math.Round(totalAmount * 100m, 0, MidpointRounding.AwayFromZero);
        var baseCents = totalCents / numberOfInstallments;
        var remainder = totalCents % numberOfInstallments;

        var list = new List<PledgeInstallment>(numberOfInstallments);
        var start = firstInstallmentDateLocal.Date;

        for (int i = 0; i < numberOfInstallments; i++)
        {
            var cents = baseCents + (i < remainder ? 1 : 0);
            var amt = cents / 100m;
            var date = AddFrequency(start, frequency, i);

            list.Add(new PledgeInstallment(amt, date, i + 1));
        }

        return list;
    }

    public static string ToApiFrequency(PledgeFrequency f) => f switch
    {
        PledgeFrequency.Weekly => "Weekly",
        PledgeFrequency.EveryTwoWeeks => "EveryTwoWeeks",
        PledgeFrequency.EveryFourWeeks => "EveryFourWeeks",
        PledgeFrequency.Monthly => "Monthly",
        PledgeFrequency.Quarterly => "Quarterly",
        PledgeFrequency.Annually => "Annually",
        _ => "Monthly"
    };

    private static DateTime AddFrequency(DateTime start, PledgeFrequency f, int offset) => f switch
    {
        PledgeFrequency.Weekly => start.AddDays(7 * offset),
        PledgeFrequency.EveryTwoWeeks => start.AddDays(14 * offset),
        PledgeFrequency.EveryFourWeeks => start.AddDays(28 * offset),
        PledgeFrequency.Monthly => start.AddMonths(1 * offset),
        PledgeFrequency.Quarterly => start.AddMonths(3 * offset),
        PledgeFrequency.Annually => start.AddYears(offset),
        _ => start.AddMonths(offset)
    };
}
