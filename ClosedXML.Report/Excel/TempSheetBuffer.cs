using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using ClosedXML.Report.Utils;

namespace ClosedXML.Report.Excel
{
    internal class TempSheetBuffer : IReportBuffer
    {
        private const string SheetName = "__temp_buffer";
        private readonly XLWorkbook _wb;
        private IXLWorksheet _sheet;
        private int _row;
        private int _clmn;
        private int _prevrow;
        private int _prevclmn;
        private int _maxClmn;
        private int _maxRow;

        public TempSheetBuffer(XLWorkbook wb)
        {
            _wb = wb;
            Init();
        }

        public IXLAddress NextAddress { get { return _sheet.Cell(_row, _clmn).Address; } }
        public IXLAddress PrevAddress { get { return _sheet.Cell(_prevrow, _prevclmn).Address; } }

        private void Init()
        {
            if (_sheet == null)
            {
                if (!_wb.TryGetWorksheet(SheetName, out _sheet))
                {
                    _sheet = _wb.AddWorksheet(SheetName);
                    _sheet.SetCalcEngineCacheExpressions(false);
                }
                //_sheet.Visibility = XLWorksheetVisibility.VeryHidden;
            }
            _row = 1;
            _clmn = 1;
            _maxRow = _prevrow = 1;
            _maxClmn = _prevclmn = 1;
            Clear();
        }

        public void WriteValue(object value, IXLStyle cellStyle, TemplateCell tempCell)
        {
            var xlCell = _sheet.Cell(_row, _clmn);

            if (value != null && value.GetType() == typeof(string) && value.ToString().StartsWith("&image="))
            {
                var imageFile = value.ToString().Substring(7);

                if (File.Exists(imageFile))
                {
                    var image = _sheet.AddPicture(imageFile);

                    image.MoveTo(xlCell.Address);
                }
                else
                {
                    xlCell.Value = $"图片路径不存在";
                }
            }
            else
            {
                xlCell.SetValue(value);
            }
            xlCell.Style = cellStyle ?? _wb.Style;
            _maxClmn = Math.Max(_maxClmn, _clmn);
            _maxRow = Math.Max(_maxRow, _row);
            ChangeAddress(_row, _clmn + 1);

            // Set the height of the current row to the height of the template row
            xlCell.WorksheetRow().Height = tempCell.XLCell.WorksheetRow().Height;
        }

        public void WriteFormulaR1C1(string formula, IXLStyle cellStyle, TemplateCell tempCell)
        {
            var xlCell = _sheet.Cell(_row, _clmn);
            xlCell.Style = cellStyle;
            xlCell.SetFormulaR1C1(formula);
            _maxClmn = Math.Max(_maxClmn, _clmn);
            _maxRow = Math.Max(_maxRow, _row);
            ChangeAddress(_row, _clmn + 1);

            // Set the height of the current row to the height of the template row
            xlCell.WorksheetRow().Height = tempCell.XLCell.WorksheetRow().Height;
        }

        public void NewRow()
        {
            if (_clmn > 1)
                _clmn--;
            ChangeAddress(_row + 1, 1);
        }

        public IXLRange GetRange(IXLAddress startAddr, IXLAddress endAddr)
        {
            return _sheet.Range(startAddr, endAddr);
        }

        public IXLCell GetCell(int row, int column)
        {
            return _sheet.Cell(row, column);
        }

        private void ChangeAddress(int row, int clmn)
        {
            _prevrow = _row;
            _prevclmn = _clmn;
            _row = row;
            _clmn = clmn;
        }

        public IXLRange CopyTo(IXLRange range)
        {
            // LastCellUsed may produce the wrong result, see https://github.com/ClosedXML/ClosedXML/issues/339
            var lastCell = _sheet.Cell(
                _sheet.LastRowUsed(true)?.RowNumber() ?? 1,
                _sheet.LastColumnUsed(true)?.ColumnNumber() ?? 1);
            var tempRng = _sheet.Range(_sheet.Cell(1, 1), lastCell);

            var rowDiff = tempRng.RowCount() - range.RowCount();
            if (rowDiff > 0)
                range.LastRow().Unsubscribed().RowAbove().Unsubscribed().InsertRowsBelow(rowDiff, true);
            else if (rowDiff < 0)
                range.Worksheet.Range(
                    range.LastRow().RowNumber() + rowDiff + 1,
                    range.FirstColumn().ColumnNumber(),
                    range.LastRow().RowNumber(),
                    range.LastColumn().ColumnNumber())
                .Delete(XLShiftDeletedCells.ShiftCellsUp);

            range.Worksheet.ConditionalFormats.Remove(c => c.Range.Intersects(range));

            var columnDiff = tempRng.ColumnCount() - range.ColumnCount();
            if (columnDiff > 0)
                range.InsertColumnsAfter(columnDiff, true);
            else if (columnDiff < 0)
                range.Worksheet.Range(
                    range.FirstRow().RowNumber(),
                    range.LastColumn().ColumnNumber() + columnDiff + 1,
                    range.LastRow().RowNumber(),
                    range.LastColumn().ColumnNumber())
                .Delete(XLShiftDeletedCells.ShiftCellsLeft);

            tempRng.CopyTo(range.FirstCell());

            var tgtSheet = range.Worksheet;
            var tgtStartRow = range.RangeAddress.FirstAddress.RowNumber;
            using (var srcRows = _sheet.Rows(tempRng.RangeAddress.FirstAddress.RowNumber, tempRng.RangeAddress.LastAddress.RowNumber))
                foreach (var row in srcRows)
                {
                    var xlRow = tgtSheet.Row(row.RowNumber() + tgtStartRow - 1);
                    xlRow.OutlineLevel = row.OutlineLevel;
                    if (row.IsHidden)
                        xlRow.Collapse();
                    else
                        xlRow.Expand();

                    // Set the height of the current row to the height of the template row
                    xlRow.Height = row.Height;
                }

            // 复制图片
            if (_sheet.Pictures != null && _sheet.Pictures.Any())
            {
                foreach (var pic in _sheet.Pictures)
                {
                    var img = tgtSheet.AddPicture(pic.ImageStream);
                    img.Placement = ClosedXML.Excel.Drawings.XLPicturePlacement.FreeFloating;

                    var imgFromCell = tgtSheet.Cell(pic.TopLeftCellAddress.RowNumber + tgtStartRow - 1, pic.TopLeftCellAddress.ColumnNumber + 1);

                    // 检查当前行是否有合并
                    var imgRange = tgtSheet.MergedRanges.FirstOrDefault(r => r.Contains(imgFromCell));

                    int rangeColCount = 0;
                    int rangeRowCount = 0;
                    if (imgRange != null)
                    {
                        rangeColCount = imgRange.ColumnCount() - 1;
                        rangeRowCount = imgRange.RowCount() - 1;
                    }

                    var imgToCell = tgtSheet.Cell(pic.TopLeftCellAddress.RowNumber + tgtStartRow + rangeRowCount, pic.TopLeftCellAddress.ColumnNumber + rangeColCount + 2);

                    img.MoveTo(imgFromCell.Address, imgToCell.Address);
                }
            }
            return range;
        }

        public void Clear()
        {
            using (var srcRows = _sheet.RowsUsed(true))
                foreach (var row in srcRows)
                {
                    row.OutlineLevel = 0;
                }
            _sheet.Clear();
        }

        public void AddConditionalFormats(IEnumerable<IXLConditionalFormat> formats, IXLRangeBase fromRange, IXLRangeBase toRange)
        {
            //var tempRng = _sheet.Range(_sheet.Cell(1, 1), _sheet.Cell(_prevrow, _prevclmn));
            foreach (var format in formats)
            {
                format.CopyRelative(fromRange, toRange, true);
            }
        }

        public void Dispose()
        {
            var namedRanges = _wb.NamedRanges
                .Where(nr => nr.Ranges.Any(r => r.Worksheet?.Name == SheetName))
                .ToList();
            namedRanges.ForEach(nr => nr.Delete());

            _wb.Worksheets.Delete(SheetName);
        }
    }
}
