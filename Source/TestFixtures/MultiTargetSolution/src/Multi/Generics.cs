namespace Repro;

public class Box<T>
{
    public Box() { }

    public T? Get() => default;

    public TResult Map<TResult>(System.Func<T, TResult> map) => map(default!);

    public T? this[int index] => default;
}

public class Pair { }

public class Pair<T> { }

public class Multi<T> { }

public class Multi<T1, T2> { }

public class Converter<T>
{
    public T? Read() => default;

    public void Parse()
    {
        void Tokenize() { }
    }
}

public class Scanner
{
    public void Read() { }
}
