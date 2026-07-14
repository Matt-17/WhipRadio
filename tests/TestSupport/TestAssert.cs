using System.Collections;
using MsAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace WhipRadio.Tests;

public static class TestAssert
{
    public static void Equal(object? expected, object? actual)
    {
        if (expected is not string
            && actual is not string
            && expected is IEnumerable expectedItems
            && actual is IEnumerable actualItems)
        {
            EqualSequences(expectedItems, actualItems);
            return;
        }

        if (IsNumeric(expected) && IsNumeric(actual))
        {
            MsAssert.AreEqual(Convert.ToDecimal(expected), Convert.ToDecimal(actual));
            return;
        }

        MsAssert.AreEqual(expected, actual);
    }

    public static void Equal(double expected, double actual, int precision)
        => MsAssert.AreEqual(expected, actual, Math.Pow(10, -precision));

    public static void Equal(float expected, float actual, int precision)
        => MsAssert.AreEqual(expected, actual, (float)Math.Pow(10, -precision));

    public static void NotEqual(object? notExpected, object? actual)
        => MsAssert.AreNotEqual(notExpected, actual);

    public static void Same(object? expected, object? actual)
        => MsAssert.AreSame(expected, actual);

    public static void True(bool condition, string? message = null)
        => MsAssert.IsTrue(condition, message);

    public static void False(bool condition, string? message = null)
        => MsAssert.IsFalse(condition, message);

    public static void Null(object? value)
        => MsAssert.IsNull(value);

    public static void NotNull(object? value)
        => MsAssert.IsNotNull(value);

    public static void Empty(IEnumerable items)
    {
        foreach (var _ in items)
        {
            MsAssert.Fail("Expected collection to be empty.");
        }
    }

    public static void Contains(string expectedSubstring, string? actualString)
    {
        if (actualString is null)
        {
            MsAssert.Fail($"Expected string to contain '{expectedSubstring}', but the string was null.");
            return;
        }

        StringAssert.Contains(actualString, expectedSubstring);
    }

    public static void Contains(string expectedSubstring, string? actualString, StringComparison comparison)
    {
        if (actualString is null)
        {
            MsAssert.Fail($"Expected string to contain '{expectedSubstring}', but the string was null.");
            return;
        }

        MsAssert.IsTrue(
            actualString.Contains(expectedSubstring, comparison),
            $"Expected string to contain '{expectedSubstring}'.");
    }

    public static void Contains<T>(T expected, IEnumerable<T> collection)
        => MsAssert.IsTrue(collection.Contains(expected), $"Expected collection to contain {expected}.");

    public static void Contains<T>(IEnumerable<T> collection, Predicate<T> match)
        => MsAssert.IsTrue(collection.Any(item => match(item)), "Expected collection to contain a matching item.");

    public static void DoesNotContain(string expectedSubstring, string? actualString)
    {
        if (actualString is null)
        {
            return;
        }

        MsAssert.IsFalse(actualString.Contains(expectedSubstring), $"Did not expect string to contain '{expectedSubstring}'.");
    }

    public static void DoesNotContain<T>(T expected, IEnumerable<T> collection)
        => MsAssert.IsFalse(collection.Contains(expected), $"Did not expect collection to contain {expected}.");

    public static void DoesNotContain<T>(IEnumerable<T> collection, Predicate<T> match)
        => MsAssert.IsFalse(collection.Any(item => match(item)), "Did not expect collection to contain a matching item.");

    public static void InRange<T>(T actual, T low, T high)
        where T : IComparable<T>
        => MsAssert.IsTrue(
            actual.CompareTo(low) >= 0 && actual.CompareTo(high) <= 0,
            $"Expected {actual} to be in range [{low}, {high}].");

    public static void All<T>(IEnumerable<T> collection, Action<T> assertion)
    {
        foreach (var item in collection)
        {
            assertion(item);
        }
    }

    public static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            MsAssert.Fail($"Expected exception {typeof(TException).Name}, but got {ex.GetType().Name}.");
        }

        MsAssert.Fail($"Expected exception {typeof(TException).Name}, but no exception was thrown.");
        return null!;
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            MsAssert.Fail($"Expected exception {typeof(TException).Name}, but got {ex.GetType().Name}.");
        }

        MsAssert.Fail($"Expected exception {typeof(TException).Name}, but no exception was thrown.");
        return null!;
    }

    public static async Task<TException> ThrowsAnyAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is TException expected)
        {
            return expected;
        }
        catch (Exception ex)
        {
            MsAssert.Fail($"Expected exception assignable to {typeof(TException).Name}, but got {ex.GetType().Name}.");
        }

        MsAssert.Fail($"Expected exception assignable to {typeof(TException).Name}, but no exception was thrown.");
        return null!;
    }

    private static void EqualSequences(IEnumerable expected, IEnumerable actual)
    {
        var index = 0;
        var expectedEnumerator = expected.GetEnumerator();
        var actualEnumerator = actual.GetEnumerator();

        try
        {
            while (true)
            {
                var hasExpected = expectedEnumerator.MoveNext();
                var hasActual = actualEnumerator.MoveNext();
                if (!hasExpected || !hasActual)
                {
                    MsAssert.AreEqual(hasExpected, hasActual, $"Sequence length differed at index {index}.");
                    return;
                }

                Equal(expectedEnumerator.Current, actualEnumerator.Current);
                index++;
            }
        }
        finally
        {
            (expectedEnumerator as IDisposable)?.Dispose();
            (actualEnumerator as IDisposable)?.Dispose();
        }
    }

    private static bool IsNumeric(object? value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal;
}
