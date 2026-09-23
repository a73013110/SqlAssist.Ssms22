using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SqlAssist.Core.Tabular;

namespace SqlAssist.Ssms22.UI;

/// <summary>資料格的純文字讀取與匯出，不依賴鍵盤焦點或已實體化的儲存格。</summary>
internal static class SqlDataGridText
{
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
        var tabular = new SqlTabularColumn<object>[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            // 不連續選取的洞保留空格，不把交叉位置上未選的內容一起複製出去。
            tabular[index] = new SqlTabularColumn<object>(column.Header?.ToString() ?? string.Empty, row =>
                !selectedOnly || selected.Contains((row, column))
                    ? reader.Read(column, row) ?? string.Empty
                    : string.Empty);
        }

        var rows = new List<object>();
        foreach (var row in grid.Items)
        {
            if (row == CollectionView.NewItemPlaceholder || (selectedOnly && !selectedRows.Contains(row)))
            {
                continue;
            }

            rows.Add(row);
        }

        // 加引號規則與清單的批次複製同一份實作；沒有資料列時不輸出孤立的表頭。
        return rows.Count == 0 ? string.Empty : SqlTabularText.ToTsv(tabular, rows);
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
}
