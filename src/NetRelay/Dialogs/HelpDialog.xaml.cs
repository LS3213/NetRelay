using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace NetRelay.Dialogs;

public partial class HelpDialog : Window
{
    private readonly IReadOnlyList<HelpTopic> _topics = BuildTopics();

    public HelpDialog()
    {
        InitializeComponent();
        SectionList.ItemsSource = _topics;
        if (_topics.Count > 0)
        {
            SectionList.SelectedIndex = 0;
        }
    }

    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not HelpTopic topic)
        {
            return;
        }

        DetailTitleText.Text = topic.Title;
        DetailSummaryText.Text = topic.Summary;
        DetailSectionsPanel.Children.Clear();

        foreach (var section in topic.Sections)
        {
            var card = new Border
            {
                Background = new MediaSolidColorBrush(MediaColor.FromArgb(0x73, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new MediaSolidColorBrush(MediaColor.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 0, 14)
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = section.Title,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = (MediaBrush)FindResource("TextPrimaryBrush")
            });

            if (!string.IsNullOrWhiteSpace(section.Intro))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = section.Intro,
                    Margin = new Thickness(0, 8, 0, 12),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 22,
                    FontSize = 12,
                    Foreground = (MediaBrush)FindResource("TextSecondaryBrush")
                });
            }

            foreach (var bullet in section.Bullets)
            {
                var bulletText = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 22,
                    FontSize = 12.5,
                    Foreground = (MediaBrush)FindResource("TextPrimaryBrush"),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                bulletText.Inlines.Add(new Run("• ") { Foreground = new MediaSolidColorBrush(MediaColor.FromRgb(0x53, 0x6E, 0xF2)), FontWeight = FontWeights.Bold });
                bulletText.Inlines.Add(new Run(bullet));
                stack.Children.Add(bulletText);
            }

            card.Child = stack;
            DetailSectionsPanel.Children.Add(card);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private static IReadOnlyList<HelpTopic> BuildTopics()
    {
        return
        [
            new HelpTopic(
                "快速开始",
                "先理解 NetRelay 的定位，再决定你是只做手动控制，还是要上自动化规则。",
                [
                    new HelpSection(
                        "这款软件到底做什么",
                        "NetRelay 当前版本的核心是“启用/禁用本机网卡”以及围绕这一动作的安全保护、自动化调度、更新和诊断能力。",
                        [
                            "它不是传统意义上的网络代理、加速器或流量转发器，也不是自动切换到另一张网卡的完整链路编排器。",
                            "它可以手动启用或禁用指定网卡，也可以按照你设定的规则在特定时间或特定网络状态下执行这些动作。",
                            "它会尽量避免因为误操作导致整机断网，例如在禁用网卡前检查是否存在可联网的备用网络，或者在禁用后发现网络不可用时自动回滚。",
                            "如果你只想手动控制网卡，可以完全不创建自动化规则；如果你要定时断开、定时恢复、探测掉线后执行动作，再用规则功能。"
                        ]),
                    new HelpSection(
                        "最短使用路径",
                        null,
                        [
                            "首次运行先完成隐私同意，设备会用脱敏后的硬件指纹完成匿名激活。",
                            "进入“网络概览”后先确认当前列出的网卡状态，再决定是否手动启用或禁用。",
                            "如果想做自动化，进入“自动化规则”新建规则，先从定时规则开始，确认行为符合预期后再使用网络状态触发规则。",
                            "如果程序行为和预期不一致，优先去“执行历史”看原因码，再去“程序设置 -> 诊断工具”导出诊断包。"
                        ])
                ]),
            new HelpTopic(
                "网络概览与手动控制",
                "这里是日常操作区，用于查看网卡状态、手动启用/禁用，以及基础安全保护。",
                [
                    new HelpSection(
                        "当前页面能做什么",
                        null,
                        [
                            "查看本机已识别网卡的名称、状态、流量与基本运行信息。",
                            "对可操作网卡执行“启用”或“禁用”。当前程序没有一键“切换网卡”功能，只有分别对单张网卡执行启用或禁用。",
                            "开启“手动禁用保护”后，手动禁用网卡前会先验证是否还有其他网卡可访问互联网，避免直接把电脑搞成彻底断网。"
                        ]),
                    new HelpSection(
                        "手动禁用保护是什么",
                        null,
                        [
                            "这是手动操作层面的保护，不是自动化规则里的保护。",
                            "开启后，你在主界面点击禁用某张网卡时，程序会先做备用网络可用性验证；如果没有可联网的备用路径，会中止本次禁用操作。",
                            "它只负责防误操作，不会主动替你启用另一张网卡。"
                        ])
                ]),
            new HelpTopic(
                "自动化规则",
                "规则是 NetRelay 的核心自动化能力，但本质仍然围绕“启用/禁用目标网卡”展开。",
                [
                    new HelpSection(
                        "规则由哪些部分组成",
                        null,
                        [
                            "规则名称：便于你识别规则用途。",
                            "目标网卡：真正会被启用或禁用的那张网卡。",
                            "动作：当前只有两种，启用目标网卡，或禁用目标网卡。",
                            "触发方式：一次性、每天、每周，或基于网络变化触发。",
                            "高级保护：包括冷却保护、备用网络保护、延迟自动恢复和提前通知预警。"
                        ]),
                    new HelpSection(
                        "当前没有的能力",
                        null,
                        [
                            "没有“自动切换到另一张网卡”的独立动作。",
                            "没有根据带宽、延迟、丢包自动择优选路。",
                            "没有链式工作流，例如先启用 A，再等待探测成功，再禁用 B。"
                        ]),
                    new HelpSection(
                        "什么时候适合用规则",
                        null,
                        [
                            "你希望在固定时间自动断开某张网卡，稍后再恢复。",
                            "你希望在探测网卡掉线后自动执行启用或禁用动作。",
                            "你希望在动作执行前收到倒计时预警，有机会延后或取消本次执行。"
                        ])
                ]),
            new HelpTopic(
                "防抖、冷却、备用保护与恢复",
                "这几个概念最容易混淆，实际作用完全不同。",
                [
                    new HelpSection(
                        "离线防抖时间",
                        null,
                        [
                            "只用于“网络变化触发”规则。",
                            "意思是探测网卡必须连续保持离线达到这段时间，程序才认定它真的掉线并触发规则。",
                            "用途是过滤瞬时抖动、短暂丢包或系统刷新造成的假离线。"
                        ]),
                    new HelpSection(
                        "冷却保护时间",
                        null,
                        [
                            "这是单条规则自己的执行冷却窗口。",
                            "规则一旦实际执行过启用或禁用动作，在冷却时间内不会再次执行同一条规则，避免短时间反复触发。",
                            "它不是网络恢复等待时间，也不是自动切换时间。",
                            "自动恢复触发后不会把这条规则的正常冷却窗口重新向后推。"
                        ]),
                    new HelpSection(
                        "启用备用网络保护",
                        null,
                        [
                            "只在规则动作是“禁用目标网卡”时有意义。",
                            "开启后，程序会在禁用前确认是否存在可联网的备用网卡；如果没有，就直接取消本次禁用。",
                            "如果禁用完成后发现备用网络实际上也不可用，程序会触发安全回滚，把刚才禁用的目标网卡重新启用。",
                            "这项保护的目的只是避免整机断网，不是帮你切换线路。"
                        ]),
                    new HelpSection(
                        "开启延迟自动恢复",
                        null,
                        [
                            "这是规则成功执行后，过一段设定时间再把目标网卡自动启用回来。",
                            "典型用法是临时断网、定时封禁或测试结束后的自动恢复。",
                            "它只会执行恢复启用，不会自动恢复成更复杂的网络拓扑。"
                        ]),
                    new HelpSection(
                        "启用提前通知预警",
                        null,
                        [
                            "规则正式执行前，主界面会出现倒计时提示。",
                            "你可以立即执行、延后执行，或者在规则允许的情况下取消本次执行。",
                            "适合用在定时断网、会议期间临时停网等容易打断当前工作的场景。"
                        ])
                ]),
            new HelpTopic(
                "执行历史与结果判断",
                "执行历史不是装饰，它是定位问题的第一入口。",
                [
                    new HelpSection(
                        "这里记录什么",
                        null,
                        [
                            "每次规则执行、跳过、失败、自动恢复和安全回滚，都会生成一条记录。",
                            "你能看到来源、目标网卡、动作结果、时间以及简化后的原因说明。"
                        ]),
                    new HelpSection(
                        "常见结果怎么理解",
                        null,
                        [
                            "规则冷却中：说明同一条规则刚执行过，还在冷却窗口内。",
                            "条件不满足：说明规则触发了，但前置条件没有成立。",
                            "无可用备用网络：说明程序为了防止整机断网，中止了禁用操作。",
                            "切换后备用网络失效，已安全回滚：说明程序尝试过禁用，但验证后发现风险，已经自动重新启用目标网卡。"
                        ])
                ]),
            new HelpTopic(
                "更新、更新历史与更新器",
                "客户端支持主更新源和 GitHub 备用源，并带独立更新器。",
                [
                    new HelpSection(
                        "检查更新怎么工作",
                        null,
                        [
                            "启动时可静默检查更新，也可以在“关于”页手动检查。",
                            "如果检测到新版本，程序会先弹出更新信息窗口，展示版本号、更新日志和是否强制更新，而不是直接开始下载。",
                            "如果当前已经是最新版本，正常情况下不会自动打扰你。"
                        ]),
                    new HelpSection(
                        "更新历史显示什么",
                        null,
                        [
                            "这里只展示当前仍处于发布状态的版本。",
                            "已撤回版本不会继续展示给客户端，避免用户把撤回版本当成可用版本参考。",
                            "每条历史都可以查看详细更新日志和更新策略。"
                        ]),
                    new HelpSection(
                        "更新器负责什么",
                        null,
                        [
                            "下载完成后，由独立更新器接管文件替换，避免主程序在自身运行时直接覆盖自己。",
                            "更新器会显示进度与完成状态，并在替换成功后重新启动主程序。",
                            "如果更新失败，优先检查更新缓存、诊断日志和当前安装目录写入权限。"
                        ]),
                    new HelpSection(
                        "关于备用源",
                        null,
                        [
                            "主更新源不可用时，客户端可以回退到 GitHub 镜像源。",
                            "备用源是兜底下载能力，不代表后台版本服务本身可省略；版本判断、更新日志和策略仍以后台发布记录为准。"
                        ])
                ]),
            new HelpTopic(
                "反馈、日志与诊断包",
                "真正排障时，反馈和诊断工具是一套完整链路。",
                [
                    new HelpSection(
                        "建议与反馈页面",
                        null,
                        [
                            "可以提交 Bug、功能建议或其他问题。",
                            "可选附带诊断日志，便于排查现场问题。",
                            "现在还支持查看“我的反馈历史与进度”，仅展示当前设备自己的提交记录，不会看到其他用户的反馈。"
                        ]),
                    new HelpSection(
                        "诊断包里通常有什么",
                        null,
                        [
                            "运行日志、执行记录、配置摘要、适配器诊断信息和更新相关状态。",
                            "日志会尽量详细到足以定位问题，但不会故意暴露敏感服务端拦截细节或原始隐私标识。"
                        ]),
                    new HelpSection(
                        "什么时候该导出诊断包",
                        null,
                        [
                            "规则没有按预期执行，但界面提示过于笼统。",
                            "更新失败、下载失败、校验失败或替换失败。",
                            "启用/禁用网卡时出现偶发性异常，且执行历史不足以解释。"
                        ])
                ]),
            new HelpTopic(
                "设置、托盘与后台运行",
                "这里主要控制程序日常运行方式，而不是功能逻辑本身。",
                [
                    new HelpSection(
                        "关闭窗口行为",
                        null,
                        [
                            "每次询问：适合刚开始使用时，避免误关。",
                            "隐藏到托盘：适合长期后台运行，这通常是最稳妥的日常模式。",
                            "直接退出：关闭窗口即结束主程序，自动化规则也不会继续在本机进程内运行。"
                        ]),
                    new HelpSection(
                        "托盘能做什么",
                        null,
                        [
                            "打开主窗口。",
                            "检查更新。",
                            "导出诊断包、打开日志目录、打开更新缓存。",
                            "直接退出程序。"
                        ]),
                    new HelpSection(
                        "规则默认值是什么意思",
                        null,
                        [
                            "程序设置里的默认防抖时间、默认冷却时间、日志保留天数，只影响你以后新建的规则或日志轮转策略。",
                            "它们不会自动修改已经存在的规则。"
                        ])
                ]),
            new HelpTopic(
                "受限模式、隐私与设备激活",
                "这是平台安全策略相关能力，和普通本地操作不同。",
                [
                    new HelpSection(
                        "受限模式是什么",
                        null,
                        [
                            "当后台策略判定设备需要限制时，客户端会进入受限模式。",
                            "受限模式下，网卡启用/禁用和后台自动调度会被停用，但你仍然可以检查更新、查看部分信息并提交申诉。"
                        ]),
                    new HelpSection(
                        "首次运行的隐私同意在做什么",
                        null,
                        [
                            "首次运行需要勾选同意后，客户端才会进行匿名设备激活。",
                            "激活使用的是基于硬件信息脱敏计算后的哈希标识，不上传原始硬件序列号，也不读取个人文件、用户名或周边 Wi‑Fi 等隐私内容。"
                        ]),
                    new HelpSection(
                        "需要避免的误区",
                        null,
                        [
                            "NetRelay 不是免配置即全自动网络编排器，很多动作仍然要求你明确指定目标网卡和策略。",
                            "如果你要的是真正的多网卡自动切换、优选、故障转移链路，当前版本还没有完整实现，应当按这个边界使用。"
                        ])
                ])
        ];
    }

    private sealed record HelpTopic(string Title, string Summary, IReadOnlyList<HelpSection> Sections);

    private sealed record HelpSection(string Title, string? Intro, IReadOnlyList<string> Bullets);
}
