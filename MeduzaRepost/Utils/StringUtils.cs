namespace MeduzaRepost;

public static class StringUtils
{
    public static string Trim(this string? str, int maxLength)
    {
        if (str is null)
            return "";
        
        if (str.Length > maxLength)
            return str[..(maxLength - 1)] + "…";

        return str;
    }
}