namespace AAEmu.ContentStudio.Core;

public sealed class ContentStudioException : Exception
{
    public ContentStudioException(string message)
        : base(message)
    {
    }

    public ContentStudioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
