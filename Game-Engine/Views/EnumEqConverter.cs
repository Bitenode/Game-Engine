using Avalonia.Data.Converters;
using Avalonia.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Game_Engine.Views
{
    public sealed class EnumEqConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is null || parameter is null) return false;
            var enumType = value.GetType();
            var param = parameter is string s ? Enum.Parse(enumType, s) : parameter;
            return value.Equals(param);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            // Only update enum when radio is checked
            if (value is bool b && b && parameter is not null)
            {
                var enumType = targetType.IsEnum ? targetType : (targetType.IsGenericType ? targetType.GenericTypeArguments[0] : null);
                if (enumType is null) return BindingOperations.DoNothing;
                return parameter is string s ? Enum.Parse(enumType, s) : parameter;
            }
            return BindingOperations.DoNothing;
        }
    }

}
