namespace ConditionalLib;

public abstract class Animal
{
}

#if DEBUG
public class Dog : Animal
{
}
#else
public class Cat : Animal
{
}
#endif
