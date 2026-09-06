using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SqlAssist.Ssms22.UI;
using Xunit;

namespace SqlAssist.Ssms22.Tests.UI;

public sealed class SqlDataGridTextTests
{
    public sealed class Row
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    [Fact]
    public void 不連續選取只複製選到的儲存格且依畫面排序()
    {
        WpfTest.Run(() =>
        {
            var first = new Row { Name = "Lib_Reader", Description = "讀者" };
            var second = new Row { Name = "Loan", Description = "借閱" };
            var grid = CreateGrid(first, second);
            grid.Items.SortDescriptions.Add(new SortDescription(nameof(Row.Name), ListSortDirection.Descending));
            grid.Columns[1].DisplayIndex = 0;
            grid.SelectedCells.Add(new DataGridCellInfo(first, grid.Columns[0]));
            grid.SelectedCells.Add(new DataGridCellInfo(second, grid.Columns[1]));

            Assert.Equal(Lines("說明\t名稱", "借閱\t", "\tLib_Reader"), SqlDataGridText.Build(grid, selectedOnly: true));
        });
    }

    [Fact]
    public void 複製全部使用已排序及篩選的檢視並略過隱藏欄()
    {
        WpfTest.Run(() =>
        {
            var grid = CreateGrid(new Row { Name = "Branch" }, new Row { Name = "Loan" }, new Row { Name = "Lib_Reader" });
            grid.Items.SortDescriptions.Add(new SortDescription(nameof(Row.Name), ListSortDirection.Descending));
            grid.Items.Filter = item => ((Row)item).Name != "Branch";
            grid.Columns[1].Visibility = Visibility.Collapsed;
            Assert.Equal(Lines("名稱", "Loan", "Lib_Reader"), SqlDataGridText.Build(grid, selectedOnly: false));
        });
    }

    [Fact]
    public void 沒有可見選取或資料時不輸出孤立表頭()
    {
        WpfTest.Run(() =>
        {
            var row = new Row { Name = "Loan" };
            var grid = CreateGrid(row);
            Assert.Empty(SqlDataGridText.Build(grid, selectedOnly: true));
            grid.SelectedCells.Add(new DataGridCellInfo(row, grid.Columns[1]));
            grid.Columns[1].Visibility = Visibility.Collapsed;
            Assert.Empty(SqlDataGridText.Build(grid, selectedOnly: true));
            grid.ItemsSource = Array.Empty<Row>();
            Assert.Empty(SqlDataGridText.Build(grid, selectedOnly: false));
        });
    }

    [Fact]
    public void 多行說明與定位字元不破壞儲存格邊界()
    {
        WpfTest.Run(() =>
        {
            var grid = CreateGrid(new Row { Name = "Loan", Description = "借閱\t\"說明\"\n第二行" });
            Assert.Equal(Lines("名稱\t說明", "Loan\t\"借閱\t\"\"說明\"\"\n第二行\""),
                SqlDataGridText.Build(grid, selectedOnly: false));
        });
    }

    [Fact]
    public void 樣板欄沿用排序屬性且不隱藏讀不出的欄()
    {
        WpfTest.Run(() =>
        {
            var rows = new[] { new Row { Name = "Loan", Description = " \n" } };
            var grid = CreateGrid(rows);
            Assert.False(SqlDataGridText.HasAnyValue(grid.Columns[1], rows));
            var template = new DataGridTemplateColumn { Header = "旗標", SortMemberPath = nameof(Row.Name) };
            grid.Columns.Add(template);
            Assert.True(SqlDataGridText.HasAnyValue(template, rows));
            Assert.Contains("Loan\t\" \n\"\tLoan", SqlDataGridText.Build(grid, selectedOnly: false));
            template.SortMemberPath = "Missing";
            Assert.True(SqlDataGridText.HasAnyValue(template, rows));
        });
    }

    private static DataGrid CreateGrid(params Row[] rows)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            SelectionUnit = DataGridSelectionUnit.Cell,
            SelectionMode = DataGridSelectionMode.Extended,
            ItemsSource = rows
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "名稱", Binding = new Binding(nameof(Row.Name)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "說明", Binding = new Binding(nameof(Row.Description)) });
        return grid;
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;
}
