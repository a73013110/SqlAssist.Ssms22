using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SqlAssist.Ssms22.UI;

/// <summary>資料格的純文字讀取與匯出，不依賴鍵盤焦點或已實體化的儲存格。</summary>
internal static class SqlDataGridText
{
    private static readonly char[] QuotedCharacters = { '\t', '\r', '\n', '"' };

    public static string Build(DataGrid grid, bool selectedOnly)
    {
        // DataGridCellInfo 還比對內部擁有者，不能拿新建的儲存格資訊比對 SelectedCells。
        var selected = new HashSet<(object Row, DataGridColumn Column)>();
        var selectedRows = new HashSet<object>();
        var selectedColumns = new HashSet<DataGridColumn>();
        if (selectedOnly)
        {
            foreach (var cell in grid.SelectedCells)
            {
                if (!cell.IsValid || cell.Column.Visibility != Visibility.Visible)
                {
                    continue;
                }

                selected.Add((cell.Item, cell.Column));
                selectedRows.Add(cell.Item);
                selectedColumns.Add(cell.Column);
            }

            if (selected.Count == 0)
            {
                return string.Empty;
            }
        }

        var columns = new List<DataGridColumn>();
        foreach (var column in grid.Columns)
        {
            if (column.Visibility == Visibility.Visible && (!selectedOnly || selectedColumns.Contains(column)))
            {
                columns.Add(column);
            }
        }

        if (columns.Count == 0)
        {
            return string.Empty;
        }

        // 欄拖曳與列排序都以畫面為準，不用原始 ItemsSource 或選取發生的順序。
        columns.Sort((left, right) => left.DisplayIndex.CompareTo(right.DisplayIndex));
        var reader = new ValueReader();
        var builder = new StringBuilder();
        var hasRows = false;
        AppendLine(builder, columns, column => column.Header?.ToString() ?? string.Empty);
        foreach (var row in grid.Items)
        {
            if (row == CollectionView.NewItemPlaceholder || (selectedOnly && !selectedRows.Contains(row)))
            {
                continue;
            }

            hasRows = true;
            // 不連續選取的洞保留空格，不把交叉位置上未選的內容一起複製出去。
            AppendLine(builder, columns, column =>
                !selectedOnly || selected.Contains((row, column))
                    ? reader.Read(column, row) ?? string.Empty
                    : string.Empty);
        }

        return hasRows ? builder.ToString() : string.Empty;
    }

    /// <summary>無法解析的欄一律保留，避免把實際有值的欄藏起來。</summary>
    public static bool HasAnyValue(DataGridColumn column, IEnumerable rows)
    {
        var reader = new ValueReader();
        foreach (var row in rows)
        {
            var value = reader.Read(column, row);
            if (value is null || !string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ValueReader
    {
        private readonly Dictionary<DataGridColumn, string> _paths = new();
        private readonly Dictionary<(Type Type, string Path), PropertyInfo?> _properties = new();

        public string? Read(DataGridColumn column, object row)
        {
            if (!_paths.TryGetValue(column, out var path))
            {
                // 徽章樣板以 SortMemberPath 指向同一份旗標的純文字版本。
                path = column is DataGridTextColumn { Binding: Binding { Path.Path: { Length: > 0 } bound } }
                    ? bound
                    : column.SortMemberPath ?? string.Empty;
                _paths.Add(column, path);
            }

            var key = (row.GetType(), path);
            if (!_properties.TryGetValue(key, out var property))
            {
                property = path.Length == 0 ? null : key.Item1.GetProperty(path);
                _properties.Add(key, property);
            }

            // 快取限定在這次操作，避免每格反射，也不把資料列或欄留在靜態快取裡。
            return property is null ? null : property.GetValue(row)?.ToString() ?? string.Empty;
        }
    }

    private static void AppendLine(
        StringBuilder builder,
        IReadOnlyList<DataGridColumn> columns,
        Func<DataGridColumn, string> select)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (index > 0)
            {
                builder.Append('\t');
            }

            var value = select(columns[index]);
            // 說明與 SQL 運算式可含換行或定位字元；TSV 引號保護原有儲存格邊界。
            if (value.IndexOfAny(QuotedCharacters) >= 0)
            {
                builder.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
            }
            else
            {
                builder.Append(value);
            }
        }

        builder.AppendLine();
    }
}
