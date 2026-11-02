namespace Torimaru.SelfTest;

/// <summary>
/// 外部 NuGet パッケージなしで動く最小テストランナー。
/// オフライン環境でも必ず `dotnet run` でテストできることを優先した。
/// </summary>
public sealed class TestRunner
{
    private readonly List<string> _failures = [];
    private int _passed;
    private string _currentCase = string.Empty;

    public void Case(string name, Action body)
    {
        _currentCase = name;
        try
        {
            body();
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        catch (AssertionException ex)
        {
            _failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL  {name}");
            Console.WriteLine($"        {ex.Message}");
        }
        catch (Exception ex)
        {
            _failures.Add($"{name}: 予期しない例外 {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  ERROR {name}");
            Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Section(string title) => Console.WriteLine($"\n[{title}]");

    public int Report()
    {
        Console.WriteLine();
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"合格: {_passed} / 失敗: {_failures.Count}");
        if (_failures.Count == 0)
        {
            Console.WriteLine("すべてのテストに合格しました。");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("失敗一覧:");
        foreach (var failure in _failures)
        {
            Console.WriteLine($"  - {failure}");
        }

        return 1;
    }

    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new AssertionException($"true を期待: {message}");
        }
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{message} — 期待値 <{expected}> / 実際 <{actual}>");
        }
    }

    public static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new AssertionException($"{message} — 期待値 <{expected}> / 実際 <{actual}> (許容 {tolerance})");
        }
    }

    public static void Null(object? value, string message)
    {
        if (value is not null)
        {
            throw new AssertionException($"null を期待: {message} — 実際 <{value}>");
        }
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new AssertionException($"null でない値を期待: {message}");
        }
    }

    public static void Throws<TException>(Action body, string message)
        where TException : Exception
    {
        try
        {
            body();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new AssertionException(
                $"{message} — {typeof(TException).Name} を期待したが {ex.GetType().Name} が発生");
        }

        throw new AssertionException($"{message} — {typeof(TException).Name} を期待したが発生しなかった");
    }

    public sealed class AssertionException(string message) : Exception(message);
}
