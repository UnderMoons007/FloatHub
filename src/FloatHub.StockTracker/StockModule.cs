using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FloatHub.Core;
using Ui = FloatHub.Core.Controls;

namespace FloatHub.StockTracker;

public sealed class StockModule : IDesktopModule
{
    private Store storage = null!;
    private Journal log = null!;
    private Action<string> open = null!;
    private StockViewModel vm = null!;
    public string Id => "StockTracker";
    public string Name => "股票追踪";
    public string Description => "追踪沪、深、京、港、美股自选证券，按交易所显示最新快照。";
    public FrameworkElement View { get; private set; } = null!;
    public IReadOnlyList<SettingsPage> Pages => [
        new("股票管理","股票追踪","\uE8F1",()=>new WatchlistPage(vm,log)),
        new("行情显示","股票追踪","\uE9D9",Display),
        new("数据设置","股票追踪","\uE774",Data)
    ];
    public void Initialize(Store storage, Journal log, Action<string> openPage)
    { this.storage = storage; this.log = log; open = openPage; }
    public void LoadConfiguration()
    {
        vm = new StockViewModel(new TencentService(), storage, log);
        View = new StockView(vm, () => open("股票管理"));
    }
    public void SetPresented(bool presented) => vm.Present(presented);
    public void SetEnabled(bool enabled) => vm.Enable(enabled);
    public void Dispose() => vm?.Dispose();

    private FrameworkElement Display()
    {
        var fields = new StackPanel();
        void Save() { vm.Save(); vm.Render(); }
        void Build()
        {
            fields.Children.Clear();
            var list = vm.Options.Fields;
            for (int i = 0; i < list.Count; i++)
            {
                int pos = i;
                var field = list[pos];
                void Move(int delta)
                {
                    int target = pos + delta;
                    (list[pos], list[target]) = (list[target], list[pos]);
                    Save(); Build();
                }
                var check = Ui.Toggle(field.Name, field.Visible, on =>
                {
                    if (!on && list.Count(f => f.Visible) == 1) { Build(); return; }
                    list[pos] = list[pos] with { Visible = on }; Save(); Build();
                });
                check.IsEnabled = !field.Visible || list.Count(f => f.Visible) > 1;
                check.Width = 89;
                var digits = Ui.Choice(new[] { "0 位小数", "1 位小数", "2 位小数", "3 位小数", "4 位小数" }, field.Precision + " 位小数", s =>
                {
                    list[pos] = list[pos] with { Precision = s[0] - '0' }; Save();
                });
                digits.MinWidth = 84; digits.Margin = new Thickness(0, 0, 12, 0);
                var up = Ui.Button("上移", () => Move(-1)); up.IsEnabled = pos > 0;
                var down = Ui.Button("下移", () => Move(1)); down.IsEnabled = pos + 1 < list.Count;
                fields.Children.Add(Ui.Row(check, digits, up, down));
            }
        }
        Build();
        var preview = new ContentControl();
        preview.SetBinding(ContentControl.ContentProperty, new Binding("Rows[0]") { Source = vm, FallbackValue = null });
        preview.ContentTemplate = (DataTemplate)View.FindResource("CapsuleTemplate");
        return Ui.Stack(Ui.Card("极简胶囊", preview, Ui.Text("上涨为红色，下跌为绿色，平盘为灰色。", 13)),
            Ui.Card("显示参数", fields), Ui.Card("成交额单位",
                Ui.Toggle("使用万、亿缩写", vm.Options.ShortUnits, on => { vm.Options.ShortUnits = on; Save(); }),
                Ui.Text("币种取自每只证券的行情记录。", 13)));
    }

    private FrameworkElement Data()
    {
        var status = Ui.Text("", 17);
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(vm.Connection)) { Source = vm });
        var last = Ui.Text("", 13);
        last.SetBinding(TextBlock.TextProperty, new Binding(nameof(vm.LastSuccess)) { Source = vm, StringFormat = "最近成功连接  {0:yyyy-MM-dd HH:mm:ss zzz}", TargetNullValue = "尚无成功连接记录" });
        var interval = Ui.Choice(new[] { "15 秒", "30 秒", "60 秒", "5 分钟" }, vm.Options.Seconds == 300 ? "5 分钟" : vm.Options.Seconds + " 秒", s =>
        { vm.Options.Seconds = s == "5 分钟" ? 300 : int.Parse(s.Split(' ')[0]); vm.Save(); vm.Restart(); });
        return Ui.Stack(Ui.Card("腾讯财经", status, last, Ui.Text("公开报价接口   qt.gtimg.cn   ·   免 API Key", 13)),
            Ui.Card("刷新间隔", interval, Ui.Text("窗口收纳、隐藏或模块停用时暂停，展开后刷新。", 13)),
            Ui.Card("报价时间与覆盖", Ui.Text("悬停胶囊可查看交易所时间和市场时段。港股使用延时档，其他市场的延迟时长未获供应方保证。"),
                Ui.Text("沪、深、京、港、NYSE、NASDAQ、NYSE American 按腾讯返回记录识别。盘前盘后标签表示市场时段，报价时间单独显示。", 13)),
            Ui.Card("接口授权", Ui.Text("当前查询无需付费凭据。公开请求额度、服务等级及商业再分发许可尚未取得供应方确认。", 13)));
    }
}
