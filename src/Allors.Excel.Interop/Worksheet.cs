// <copyright file="Worksheet.cs" company="Allors bvba">
// Copyright (c) Allors bvba. All rights reserved.
// Licensed under the LGPL license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Allors.Excel.Embedded
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Allors.Excel;
    using Microsoft.Office.Interop.Excel;
    using Polly;
    using InteropWorksheet = Microsoft.Office.Interop.Excel.Worksheet;

    public interface IEmbeddedWorksheet : IWorksheet
    {
        void AddDirtyValue(Cell cell);

        void AddDirtyFormula(Cell cell);

        void AddDirtyComment(Cell cell);

        void AddDirtyStyle(Cell cell);

        void AddDirtyNumberFormat(Cell cell);

        void AddDirtyOptions(Cell cell);

        void AddDirtyRow(Row row);
    }

    public class Worksheet : IEmbeddedWorksheet
    {
        private readonly Dictionary<int, Row> rowByIndex;

        private readonly Dictionary<int, Column> columnByIndex;
        private bool isActive;

        public Worksheet(Workbook workbook, InteropWorksheet interopWorksheet)
        {
            this.Workbook = workbook;
            this.InteropWorksheet = interopWorksheet;
            this.rowByIndex = new Dictionary<int, Row>();
            this.columnByIndex = new Dictionary<int, Column>();
            this.CellByRowColumn = new Dictionary<string, Cell>();
            this.DirtyValueCells = new HashSet<Cell>();
            this.DirtyCommentCells = new HashSet<Cell>();
            this.DirtyStyleCells = new HashSet<Cell>();
            this.DirtyOptionCells = new HashSet<Cell>();
            this.DirtyNumberFormatCells = new HashSet<Cell>();
            this.DirtyFormulaCells = new HashSet<Cell>();
            this.DirtyRows = new HashSet<Row>();

            interopWorksheet.Change += this.InteropWorksheet_Change;

            ((DocEvents_Event)interopWorksheet).Activate += () =>
            {
                this.isActive = true;
                this.SheetActivated?.Invoke(this, this.Name);
            };

            ((DocEvents_Event)interopWorksheet).Deactivate += () => this.isActive = false;
        }

        public event EventHandler<CellChangedEvent> CellsChanged;

        public event EventHandler<string> SheetActivated;

        public int Index => this.InteropWorksheet.Index;

        public bool IsActive
        {
            get => this.isActive;
            set
            {
                if (value)
                {
                    this.InteropWorksheet.Activate();
                }
                else
                {
                    this.isActive = false;
                }
            }
        }

        public Workbook Workbook { get; set; }

        public InteropWorksheet InteropWorksheet { get; set; }

        public string Name { get => this.InteropWorksheet.Name; set => this.InteropWorksheet.Name = value; }

        IWorkbook IWorksheet.Workbook => this.Workbook;

        private Dictionary<string, Cell> CellByRowColumn { get; }

        private HashSet<Cell> DirtyValueCells { get; set; }

        private HashSet<Cell> DirtyCommentCells { get; set; }

        private HashSet<Cell> DirtyStyleCells { get; set; }

        private HashSet<Cell> DirtyOptionCells { get; set; }

        private HashSet<Cell> DirtyNumberFormatCells { get; set; }

        private HashSet<Cell> DirtyFormulaCells { get; set; }

        private HashSet<Row> DirtyRows { get; set; }

        public async Task RefreshPivotTables(string sourceDataRange = null)
        {
            var pivotTables = (PivotTables)this.InteropWorksheet.PivotTables();

            foreach (PivotTable pivotTable in pivotTables)
            {
                if (!string.IsNullOrWhiteSpace(sourceDataRange))
                {
                    pivotTable.SourceData = sourceDataRange;
                }

                pivotTable.RefreshTable();
            }

            await Task.CompletedTask;
        }

        public ICell this[int row, int column]
        {
            get
            {
                var key = $"{row}:{column}";
                if (!this.CellByRowColumn.TryGetValue(key, out var cell))
                {
                    cell = new Cell(this, this.Row(row), this.Column(column));
                    this.CellByRowColumn.Add(key, cell);
                }

                return cell;
            }
        }

        public static string ExcelColumnFromNumber(int column)
        {
            string columnString = string.Empty;
            decimal columnNumber = column;
            while (columnNumber > 0)
            {
                decimal currentLetterNumber = (columnNumber - 1) % 26;
                char currentLetter = (char)(currentLetterNumber + 65);
                columnString = currentLetter + columnString;
                columnNumber = (columnNumber - (currentLetterNumber + 1)) / 26;
            }

            return columnString;
        }

        IRow IWorksheet.Row(int index) => this.Row(index);

        IColumn IWorksheet.Column(int index) => this.Column(index);

        public Row Row(int index)
        {
            if (index < 0)
            {
                throw new ArgumentException("Index can not be negative", nameof(this.Row));
            }

            if (!this.rowByIndex.TryGetValue(index, out var row))
            {
                row = new Row(this, index);
                this.rowByIndex.Add(index, row);
            }

            return row;
        }

        public Column Column(int index)
        {
            if (index < 0)
            {
                throw new ArgumentException(nameof(this.Column));
            }

            if (!this.columnByIndex.TryGetValue(index, out var column))
            {
                column = new Column(this, index);
                this.columnByIndex.Add(index, column);
            }

            return column;
        }

        public async Task Flush()
        {
            var calculation = this.Workbook.InteropWorkbook.Application.Calculation;
            if (calculation != XlCalculation.xlCalculationManual)
            {
                this.Workbook.InteropWorkbook.Application.Calculation = XlCalculation.xlCalculationManual;
            }

            this.Workbook.InteropWorkbook.Application.ScreenUpdating = false;
            this.Workbook.InteropWorkbook.Application.EnableEvents = false;
            this.Workbook.InteropWorkbook.Application.DisplayStatusBar = false;
            this.Workbook.InteropWorkbook.Application.PrintCommunication = false;

            var enableFormatConditionsCalculation = this.InteropWorksheet.EnableFormatConditionsCalculation;

            if (enableFormatConditionsCalculation)
            {
                this.InteropWorksheet.EnableFormatConditionsCalculation = false;
            }

            try
            {
                this.RenderNumberFormat(this.DirtyNumberFormatCells);
                this.DirtyNumberFormatCells = new HashSet<Cell>();

                this.RenderValue(this.DirtyValueCells);
                this.DirtyValueCells = new HashSet<Cell>();

                this.RenderFormula(this.DirtyFormulaCells);
                this.DirtyFormulaCells = new HashSet<Cell>();

                this.RenderComments(this.DirtyCommentCells);
                this.DirtyCommentCells = new HashSet<Cell>();

                this.RenderStyle(this.DirtyStyleCells);
                this.DirtyStyleCells = new HashSet<Cell>();

                this.SetOptions(this.DirtyOptionCells);
                this.DirtyOptionCells = new HashSet<Cell>();

                this.UpdateRows(this.DirtyRows);
                this.DirtyRows = new HashSet<Row>();
            }
            finally
            {
                this.Workbook.InteropWorkbook.Application.Calculation = calculation;
                this.Workbook.InteropWorkbook.Application.ScreenUpdating = true;
                this.Workbook.InteropWorkbook.Application.EnableEvents = true;
                this.Workbook.InteropWorkbook.Application.DisplayStatusBar = true;
                this.Workbook.InteropWorkbook.Application.PrintCommunication = true;

                this.InteropWorksheet.EnableFormatConditionsCalculation = enableFormatConditionsCalculation;

                try
                {
                    // Recalculate when required. Formulas need to be resolved.
                    if (calculation == XlCalculation.xlCalculationAutomatic)
                    {
                        this.InteropWorksheet.Calculate();
                    }
                }
                catch
                {
                }
            }

            await Task.CompletedTask;
        }

        public void AddDirtyNumberFormat(Cell cell)
        {
            this.DirtyNumberFormatCells.Add(cell);
        }

        public void AddDirtyValue(Cell cell)
        {
            this.DirtyValueCells.Add(cell);
        }

        public void AddDirtyFormula(Cell cell)
        {
            this.DirtyFormulaCells.Add(cell);
        }

        public void AddDirtyComment(Cell cell)
        {
            this.DirtyCommentCells.Add(cell);
        }

        public void AddDirtyStyle(Cell cell)
        {
            this.DirtyStyleCells.Add(cell);
        }

        public void AddDirtyOptions(Cell cell)
        {
            this.DirtyOptionCells.Add(cell);
        }

        public void AddDirtyRow(Row row)
        {
            this.DirtyRows.Add(row);
        }

        private void InteropWorksheet_Change(Microsoft.Office.Interop.Excel.Range target)
        {
            List<Cell> cells = null;
            foreach (Microsoft.Office.Interop.Excel.Range targetCell in target.Cells)
            {
                var row = targetCell.Row - 1;
                var column = targetCell.Column - 1;
                var cell = (Cell)this[row, column];

                if (cell.UpdateValue(targetCell.Value2))
                {
                    if (cells == null)
                    {
                        cells = new List<Cell>();
                    }

                    cells.Add(cell);
                }
            }

            if (cells != null)
            {
                this.CellsChanged?.Invoke(this, new CellChangedEvent(cells.Cast<ICell>().ToArray()));
            }
        }

        private void RenderValue(IEnumerable<Cell> cells)
        {
            var chunks = cells.Chunks((v, w) => true);

            Parallel.ForEach(
                chunks,
                chunk =>
                {
                    var values = new object[chunk.Count, chunk[0].Count];
                    for (var i = 0; i < chunk.Count; i++)
                    {
                        for (var j = 0; j < chunk[0].Count; j++)
                        {
                            values[i, j] = chunk[i][j].Value;
                        }
                    }

                    var fromRow = chunk.First().First().Row;
                    var fromColumn = chunk.First().First().Column;

                    var toRow = chunk.Last().Last().Row;
                    var toColumn = chunk.Last().Last().Column;

                    var range = this.WaitAndRetry(() =>
                    {
                        var from = (Microsoft.Office.Interop.Excel.Range)this.InteropWorksheet.Cells[fromRow.Index + 1, fromColumn.Index + 1];
                        var to = (Microsoft.Office.Interop.Excel.Range)this.InteropWorksheet.Cells[toRow.Index + 1, toColumn.Index + 1];
                        return this.InteropWorksheet.Range[from, to];
                    });

                    this.WaitAndRetry(() =>
                    {
                        range.Value2 = values;
                    });
                });
        }

        private void RenderFormula(IEnumerable<Cell> cells)
        {
            var chunks = cells.Chunks((v, w) => true);

            Parallel.ForEach(
                chunks,
                chunk =>
                {
                    var formulas = new object[chunk.Count, chunk[0].Count];
                    for (var i = 0; i < chunk.Count; i++)
                    {
                        for (var j = 0; j < chunk[0].Count; j++)
                        {
                            formulas[i, j] = chunk[i][j].Formula;
                        }
                    }

                    var fromRow = chunk.First().First().Row;
                    var fromColumn = chunk.First().First().Column;

                    var toRow = chunk.Last().Last().Row;
                    var toColumn = chunk.Last().Last().Column;

                    var range = this.WaitAndRetry(() =>
                    {
                        var from = (Microsoft.Office.Interop.Excel.Range)this.InteropWorksheet.Cells[fromRow.Index + 1, fromColumn.Index + 1];
                        var to = (Microsoft.Office.Interop.Excel.Range)this.InteropWorksheet.Cells[toRow.Index + 1, toColumn.Index + 1];
                        return this.InteropWorksheet.Range[from, to];
                    });

                    this.WaitAndRetry(() =>
                    {
                        range.Formula = formulas;
                    });
                });
        }

        private void RenderComments(IEnumerable<Cell> cells)
        {
            Parallel.ForEach(
                cells,
                cell =>
                {
                    var range = this.WaitAndRetry(() =>
                    {
                        return (Microsoft.Office.Interop.Excel.Range)this.InteropWorksheet.Cells[cell.Row.Index + 1, cell.Column.Index + 1];
                    });

                    this.WaitAndRetry(() =>
                    {
                        if (range.Comment == null)
                        {
                            var comment = range.AddComment(cell.Comment);
                            comment.Shape.TextFrame.AutoSize = true;
                        }
                        else
                        {
                            range.Comment.Text(cell.Comment);
                        }
                    });
                });
        }

        private void RenderStyle(IEnumerable<Cell> cells)
        {
            var chunks = cells.Chunks((v, w) => Equals(v.Style, w.Style));

            Parallel.ForEach(
                chunks,
                chunk =>
                {
                    var fromRow = chunk.First().First().Row;
                    var fromColumn = chunk.First().First().Column;

                    var toRow = chunk.Last().Last().Row;
                    var toColumn = chunk.Last().Last().Column;

                    var range = this.WaitAndRetry(() =>
                    {
                        var from = this.InteropWorksheet.Cells[fromRow.Index + 1, fromColumn.Index + 1];
                        var to = this.InteropWorksheet.Cells[toRow.Index + 1, toColumn.Index + 1];
                        return this.InteropWorksheet.Range[from, to];
                    });

                    this.WaitAndRetry(() =>
                    {
                        var cc = chunk[0][0];
                        if (cc.Style != null)
                        {
                            range.Interior.Color = ColorTranslator.ToOle(chunk[0][0].Style.BackgroundColor);
                        }
                        else
                        {
                            range.Interior.ColorIndex = Microsoft.Office.Interop.Excel.XlColorIndex.xlColorIndexAutomatic;
                        }
                    });
                });
        }

        private void RenderNumberFormat(IEnumerable<Cell> cells)
        {
            var chunks = cells.Chunks((v, w) => Equals(v.NumberFormat, w.NumberFormat));

            Parallel.ForEach(
                chunks,
                chunk =>
                {
                    var fromRow = chunk.First().First().Row;
                    var fromColumn = chunk.First().First().Column;

                    var toRow = chunk.Last().Last().Row;
                    var toColumn = chunk.Last().Last().Column;

                    var range = this.WaitAndRetry(() =>
                    {
                        var from = this.InteropWorksheet.Cells[fromRow.Index + 1, fromColumn.Index + 1];
                        var to = this.InteropWorksheet.Cells[toRow.Index + 1, toColumn.Index + 1];
                        return this.InteropWorksheet.Range[from, to];
                    });

                    this.WaitAndRetry(() =>
                    {
                        range.NumberFormat = chunk[0][0].NumberFormat;
                    });
                });
        }

        private void SetOptions(IEnumerable<Cell> cells)
        {
            var chunks = cells.Chunks((v, w) => Equals(v.Options, w.Options));

            Parallel.ForEach(
                chunks,
                chunk =>
                {
                    var fromRow = chunk.First().First().Row;
                    var fromColumn = chunk.First().First().Column;

                    var toRow = chunk.Last().Last().Row;
                    var toColumn = chunk.Last().Last().Column;

                    var range = this.WaitAndRetry(() =>
                    {
                        var from = this.InteropWorksheet.Cells[fromRow.Index + 1, fromColumn.Index + 1];
                        var to = this.InteropWorksheet.Cells[toRow.Index + 1, toColumn.Index + 1];
                        return this.InteropWorksheet.Range[from, to];
                    });

                    this.WaitAndRetry(() =>
                    {
                        var cc = chunk[0][0];
                        if (cc.Options != null)
                        {
                            var validationRange = cc.Options.Name;
                            if (string.IsNullOrEmpty(validationRange))
                            {
                                if (cc.Options.Columns.HasValue)
                                {
                                    validationRange = $"{cc.Options.Worksheet.Name}!${ExcelColumnFromNumber(cc.Options.Column + 1)}${cc.Options.Row + 1}:${ExcelColumnFromNumber(cc.Options.Column + cc.Options.Columns.Value)}${cc.Options.Row + 1}";
                                }
                                else if (cc.Options.Rows.HasValue)
                                {
                                    validationRange = $"{cc.Options.Worksheet.Name}!${ExcelColumnFromNumber(cc.Options.Column + 1)}${cc.Options.Row + 1}:${ExcelColumnFromNumber(cc.Options.Column + 1)}${cc.Options.Row + cc.Options.Rows}";
                                }
                            }

                            try
                            {
                                range.Validation.Delete();
                            }
                            catch (Exception)
                            {
                            }

                            range.Validation.Add(XlDVType.xlValidateList, XlDVAlertStyle.xlValidAlertStop, Type.Missing, $"={validationRange}", Type.Missing);
                            range.Validation.IgnoreBlank = !cc.IsRequired;
                            range.Validation.InCellDropdown = !cc.HideInCellDropdown;
                        }
                        else
                        {
                            try
                            {
                                range.Validation.Delete();
                            }
                            catch (Exception)
                            {
                            }
                        }
                    });
                });
        }

        private void UpdateRows(HashSet<Row> dirtyRows)
        {
            var chunks = dirtyRows.OrderBy(w => w.Index).Aggregate(
                        new List<IList<Row>> { new List<Row>() },
                        (acc, w) =>
                        {
                            var list = acc[acc.Count - 1];
                            if (list.Count == 0 || (list[list.Count - 1].Hidden == w.Hidden))
                            {
                                list.Add(w);
                            }
                            else
                            {
                                list = new List<Row> { w };
                                acc.Add(list);
                            }

                            return acc;
                        });

            var updateChunks = chunks.Where(v => v.Count > 0);

            Parallel.ForEach(
                updateChunks,
                chunk =>
                {
                    var fromChunk = chunk.First();
                    var toChunk = chunk.Last();
                    var hidden = fromChunk.Hidden;

                    string from = $"$A${fromChunk.Index + 1}";
                    string to = $"$A${toChunk.Index + 1}";

                    var range = this.WaitAndRetry(() =>
                    {
                        return this.InteropWorksheet.Range[from, to];
                    });

                    this.WaitAndRetry(() =>
                    {
                        range.EntireRow.Hidden = hidden;
                    });
                });
        }

        private void WaitAndRetry(System.Action method, int waitTime = 100, int maxRetries = 10)
        {
            Policy
            .Handle<System.Runtime.InteropServices.COMException>()
            .WaitAndRetry(
                maxRetries,
                (retryCount) =>
                {
                    // returns the waitTime for the onRetry
                    return TimeSpan.FromMilliseconds(waitTime * retryCount);
                })
                .Execute(method);
        }

        private T WaitAndRetry<T>(System.Func<T> method, int waitTime = 100, int maxRetries = 10)
        {
            return Policy
             .Handle<System.Runtime.InteropServices.COMException>()
             .WaitAndRetry(
                 maxRetries,
                 (retryCount) =>
                 {
                     // returns the waitTime for the onRetry
                     return TimeSpan.FromMilliseconds(waitTime * retryCount);
                 })
             .Execute(method);
        }

        /// <summary>
        /// Adds a Picture on the specified rectangle. <seealso cref="GetRectangle(string)"/>
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="location"></param>
        /// <param name="size"></param>
        public void AddPicture(string fileName, System.Drawing.Rectangle rectangle)
        {
            this.Workbook.AddIn.Office.AddPicture(this.InteropWorksheet, fileName, rectangle);

            try
            {
                File.Delete(fileName);
            }
            catch
            {
                // left blank: delete temp file may fail.
            }
        }              

        /// <summary>
        /// Gets the Rectangle of a namedRange (uses the Range.MergeArea as reference). 
        /// NamedRange must exist on Workbook.
        /// </summary>
        /// <param name="namedRange"></param>
        /// <returns></returns>
        public System.Drawing.Rectangle GetRectangle(string namedRange)
        {
            Name name = this.Workbook.InteropWorkbook.Names.Item(namedRange);                    

            var area = name.RefersToRange.MergeArea;

            int left = Convert.ToInt32(area.Left);
            int top = Convert.ToInt32(area.Top);
            int width = Convert.ToInt32(area.Width);
            int height = Convert.ToInt32(area.Height);

            return new System.Drawing.Rectangle(left, top, width, height);
        }

        public Excel.Range[] GetNamedRanges()
        {
            var ranges = new List<Excel.Range>();

            foreach (Microsoft.Office.Interop.Excel.Name namedRange in this.InteropWorksheet.Names)
            {
                try
                {
                    var refersToRange = namedRange.RefersToRange;
                    if (refersToRange != null)
                    {
                        ranges.Add(new Excel.Range(refersToRange.Row - 1, refersToRange.Column - 1, refersToRange.Rows.Count, refersToRange.Columns.Count, worksheet: this,  name: namedRange.Name));
                    }
                }
                catch
                {
                    // RefersToRange can throw exception
                }
            }

            return ranges.ToArray();
        }

        /// <summary>
        /// Adds a NamedRange that has its scope on the Worksheet.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="range"></param>
        public void SetNamedRange(string name, Excel.Range range)
        {
            if (!string.IsNullOrWhiteSpace(name) && range != null)
            {
                try
                {

                    var interopWorksheet = ((Worksheet)range.Worksheet).InteropWorksheet;

                    if (interopWorksheet != null)
                    {
                        var topLeft = interopWorksheet.Cells[range.Row + 1, range.Column + 1];
                        var bottomRight = interopWorksheet.Cells[range.Row + range.Rows, range.Column + range.Columns];

                        var refersTo = interopWorksheet.Range[topLeft, bottomRight];

                        // When it does not exist, add it, else we update the range.
                        if (interopWorksheet.Names
                            .Cast<Microsoft.Office.Interop.Excel.Name>()
                            .Any(v => string.Equals(v.Name, name)))
                        {
                            interopWorksheet.Names.Item(name).RefersTo = refersTo;
                        }
                        else
                        {
                            interopWorksheet.Names.Add(name, refersTo);
                        }
                    }
                }
                catch
                {
                    // can throw exception, we dont care.
                }
            }
        }
    }
}
