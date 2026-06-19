namespace ConditionalLib;

public interface IShape
{
}

#if DEBUG
public class Square : IShape
{
}
#else
public class Circle : IShape
{
}
#endif
