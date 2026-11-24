namespace TruancyStrategy;

/// <summary>
/// Байесовский прогульщик.
/// <para>
/// Для каждого преподавателя студент держит распределение по всем возможным правилам: «случайно 50%»,
/// 6 вариантов «спросить, если вчера спросили по A» и 15 вариантов «спросить, если вчера спросили ровно по одному
/// из A и Б». Априори три типа правил равновероятны. Каждый прожитый день детерминированные гипотезы, ошибившиеся
/// в предсказании, отбрасываются, а вес «случайной» гипотезы уменьшается вдвое (она объясняет любой день лишь с
/// вероятностью 1/2). Так получается вероятность p того, что сегодня по предмету спросят.
/// </para>
/// <para>
/// Пару стоит прогулять, если ожидаемый выигрыш больше ожидаемой потери: (1 − p) · 1 &gt; p · «на кону», где «на кону» —
/// всё накопленное удовольствие плюс то, что студент ещё получит до конца семестра, если не вылетит.
/// </para>
/// <para>
/// Про прогулянные пары раннер не сообщает, спрашивали ли там. Но раз студент не вылетел, значит, не спрашивали —
/// стратегия выводит это сама. Состояние между вызовами не хранится: всё восстанавливается из истории,
/// поэтому один экземпляр можно использовать для любого числа семестров подряд.
/// </para>
/// </summary>
public sealed class BayesianSkipStrategy : ISkipStrategy
{
    private const int SubjectsCount = 6;
    private const int DaysCount = 100;
    private const int SkipPleasure = 1;
    private const int PiePleasure = 1;

    private const double RandomAskProbability = 0.5;
    private const double RandomRulePrior = 1.0 / 3;
    private const double FollowRulePrior = 1.0 / 3 / SubjectsCount;   // 6 вариантов предмета A
    private const double XorRulePrior = 1.0 / 3 / 15;                 // 15 пар {A, Б}

    // Детерминированные гипотезы (по биту на каждую): сначала 6 правил «вчера A», затем 15 правил «вчера A xor Б».
    private static readonly double[] HypothesisPriors = CreatePriors();

    // Для каждого из 64 вариантов «кого спросили вчера» — маска гипотез, по которым сегодня спросят.
    private static readonly ulong[] PredictingAsk = CreatePredictions();

    private static readonly ulong AllHypotheses = (1UL << HypothesisPriors.Length) - 1;

    // Нумерация дней раннера: с 0 или с 1. Понимаем по первому же вызову с day == 0.
    private volatile bool _zeroBasedDays;

    public string Name => "Байесовский прогульщик";

    public bool[] DecideDay(int day, IReadOnlyStudentHistory history)
    {
        if (day == 0)
            _zeroBasedDays = true;
        var firstDay = _zeroBasedDays ? 0 : 1;

        var consistent = new ulong[SubjectsCount];
        var daysAsked = new int[SubjectsCount];
        var daysNotAsked = new int[SubjectsCount];
        Array.Fill(consistent, AllHypotheses);

        var pleasure = 0;
        var askedYesterday = 0; // в первый день считается, что вчера никого не спрашивали
        for (var past = firstDay; past < day; past++)
        {
            var askedToday = 0;
            for (var subject = 0; subject < SubjectsCount; subject++)
            {
                if (history.Attended(past, (Subject)subject))
                {
                    if (history.WasAsked(past, (Subject)subject) == true)
                        askedToday |= 1 << subject;
                }
                else
                {
                    // Прогулял и не вылетел — значит, не спрашивали.
                    pleasure += SkipPleasure;
                }
            }

            pleasure += PiePleasure;

            var predictingAsk = PredictingAsk[askedYesterday];
            for (var subject = 0; subject < SubjectsCount; subject++)
            {
                if ((askedToday & (1 << subject)) != 0)
                {
                    consistent[subject] &= predictingAsk;
                    daysAsked[subject]++;
                }
                else
                {
                    consistent[subject] &= ~predictingAsk;
                    daysNotAsked[subject]++;
                }
            }

            askedYesterday = askedToday;
        }

        var daysPassed = day - firstDay;
        var remainingDays = Math.Max(0, DaysCount - daysPassed - 1);

        var randomWeight = new double[SubjectsCount];
        var totalWeight = new double[SubjectsCount];
        var expectedSkipRate = new double[SubjectsCount];
        var expectedSkipsPerDay = 0.0;
        for (var subject = 0; subject < SubjectsCount; subject++)
        {
            var observedDays = daysAsked[subject] + daysNotAsked[subject];
            randomWeight[subject] = RandomRulePrior * Math.Pow(RandomAskProbability, observedDays);
            var deterministicWeight = Prior(consistent[subject]);
            totalWeight[subject] = randomWeight[subject] + deterministicWeight;

            // Сколько пар этого преподавателя в день удастся прогуливать дальше: вероятность того, что правило
            // не случайное, умноженная на долю дней, когда он молчит (оценка Лапласа).
            expectedSkipRate[subject] = deterministicWeight / totalWeight[subject]
                * (daysNotAsked[subject] + 1.0) / (observedDays + 2.0);
            expectedSkipsPerDay += expectedSkipRate[subject];
        }

        var attend = new bool[SubjectsCount];
        for (var subject = 0; subject < SubjectsCount; subject++)
        {
            var askProbability = (RandomAskProbability * randomWeight[subject]
                + Prior(consistent[subject] & PredictingAsk[askedYesterday])) / totalWeight[subject];

            var futurePleasurePerDay = PiePleasure
                + (expectedSkipsPerDay - expectedSkipRate[subject]) * SkipPleasure;
            var atStake = pleasure + PiePleasure + remainingDays * futurePleasurePerDay;

            var skip = (1 - askProbability) * SkipPleasure > askProbability * atStake;
            attend[subject] = !skip;
        }

        return attend;
    }

    private static double Prior(ulong hypotheses)
    {
        var prior = 0.0;
        for (var i = 0; i < HypothesisPriors.Length; i++)
        {
            if (((hypotheses >> i) & 1) != 0)
                prior += HypothesisPriors[i];
        }

        return prior;
    }

    private static double[] CreatePriors()
    {
        var priors = new List<double>();
        for (var a = 0; a < SubjectsCount; a++)
            priors.Add(FollowRulePrior);
        for (var a = 0; a < SubjectsCount; a++)
        for (var b = a + 1; b < SubjectsCount; b++)
            priors.Add(XorRulePrior);
        return priors.ToArray();
    }

    private static ulong[] CreatePredictions()
    {
        var predictions = new ulong[1 << SubjectsCount];
        for (var yesterday = 0; yesterday < predictions.Length; yesterday++)
        {
            var mask = 0UL;
            var bit = 0;
            for (var a = 0; a < SubjectsCount; a++, bit++)
            {
                if (((yesterday >> a) & 1) != 0)
                    mask |= 1UL << bit;
            }

            for (var a = 0; a < SubjectsCount; a++)
            for (var b = a + 1; b < SubjectsCount; b++, bit++)
            {
                if ((((yesterday >> a) ^ (yesterday >> b)) & 1) != 0)
                    mask |= 1UL << bit;
            }

            predictions[yesterday] = mask;
        }

        return predictions;
    }
}
