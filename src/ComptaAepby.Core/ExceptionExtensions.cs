namespace ComptaAepby.Core;

public static class ExceptionExtensions
{
    /// <summary>Exception d'origine (la plus interne) : c'est elle qui porte le message utile des erreurs de base de données.</summary>
    public static Exception Innermost(this Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }

    public static string InnermostMessage(this Exception ex) => ex.Innermost().Message;
}
