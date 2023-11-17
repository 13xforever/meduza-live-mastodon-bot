using System.Reflection;

namespace MeduzaRepost;

public static class PtsExtractor
{
    public static int GetPts(this object obj)
        => obj.GetType().GetMember("pts", BindingFlags.Instance | BindingFlags.Public) switch
        {
            [FieldInfo mi] when mi.FieldType == typeof(int) => (int)mi.GetValue(obj)!,
            [PropertyInfo pi] when pi.PropertyType == typeof(int) => (int)pi.GetValue(obj)!,
            _ => 0
        };
}