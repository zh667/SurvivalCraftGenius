using Engine;
using Game;
using SurvivalcraftGenius.Mod;

namespace SurvivalcraftGenius.UI;

/// <summary>
/// In-game chat window: conversation log, input box, summon/dismiss/settings.
/// Opened with the G key; polls the player component's chat log every frame.
/// </summary>
public sealed class GeniusChatDialog : Dialog
{
    private const int VisibleLines = 14;
    private const int WrapWidth = 44;

    private static readonly Color PanelColor = new(20, 26, 30, 242);
    private static readonly Color OutlineColor = new(96, 120, 128);
    private static readonly Color PlayerColor = new(255, 224, 160);
    private static readonly Color GeniusColor = new(168, 230, 160);
    private static readonly Color InfoColor = new(150, 160, 170);

    private readonly GeniusPlayerComponent _component;
    private readonly Action _onClosed;
    private readonly LabelWidget[] _logLines = new LabelWidget[VisibleLines];
    private readonly TextBoxWidget _inputBox;
    private readonly BevelledButtonWidget _sendButton;
    private readonly BevelledButtonWidget _summonButton;
    private readonly BevelledButtonWidget _settingsButton;
    private readonly BevelledButtonWidget _closeButton;
    private readonly LabelWidget _statusLabel;
    private int _renderedVersion = -1;
    private bool _closed;

    public GeniusChatDialog(GeniusPlayerComponent component, Action onClosed)
    {
        _component = component ?? throw new ArgumentNullException(nameof(component));
        _onClosed = onClosed ?? throw new ArgumentNullException(nameof(onClosed));

        var root = new CanvasWidget
        {
            Size = new Vector2(780f, 560f),
            HorizontalAlignment = WidgetAlignment.Center,
            VerticalAlignment = WidgetAlignment.Center,
        };
        Children.Add(root);
        root.Children.Add(new RectangleWidget
        {
            FillColor = PanelColor,
            OutlineColor = OutlineColor,
        });

        var stack = new StackPanelWidget
        {
            Direction = LayoutDirection.Vertical,
            Margin = new Vector2(16f, 10f),
            HorizontalAlignment = WidgetAlignment.Center,
        };
        root.Children.Add(stack);

        stack.Children.Add(new LabelWidget
        {
            Text = "Genius · 守护灵",
            FontScale = 0.9f,
            Color = new Color(220, 235, 220),
            Size = new Vector2(740f, 36f),
        });

        for (var i = 0; i < VisibleLines; i++)
        {
            _logLines[i] = new LabelWidget
            {
                Text = "",
                FontScale = 0.62f,
                Color = InfoColor,
                Size = new Vector2(740f, 24f),
            };
            stack.Children.Add(_logLines[i]);
        }

        _statusLabel = new LabelWidget
        {
            Text = "",
            FontScale = 0.58f,
            Color = InfoColor,
            Size = new Vector2(740f, 22f),
        };
        stack.Children.Add(_statusLabel);

        var inputRow = new StackPanelWidget
        {
            Direction = LayoutDirection.Horizontal,
            Margin = new Vector2(0f, 6f),
        };
        stack.Children.Add(inputRow);
        var inputHost = new CanvasWidget { Size = new Vector2(560f, 46f) };
        inputHost.Children.Add(new RectangleWidget
        {
            FillColor = new Color(8, 12, 14),
            OutlineColor = OutlineColor,
        });
        _inputBox = new TextBoxWidget
        {
            Size = new Vector2(540f, 36f),
            MaximumLength = 500,
            FontScale = 0.7f,
            Color = new Color(235, 240, 235),
            HorizontalAlignment = WidgetAlignment.Center,
            VerticalAlignment = WidgetAlignment.Center,
            HasFocus = true,
        };
        _inputBox.Enter += _ => Send();
        inputHost.Children.Add(_inputBox);
        inputRow.Children.Add(inputHost);
        _sendButton = MakeButton("发送", 120f);
        inputRow.Children.Add(_sendButton);

        var buttonRow = new StackPanelWidget
        {
            Direction = LayoutDirection.Horizontal,
            Margin = new Vector2(0f, 6f),
        };
        stack.Children.Add(buttonRow);
        _summonButton = MakeButton("召唤", 150f);
        _settingsButton = MakeButton("设置", 150f);
        _closeButton = MakeButton("关闭", 150f);
        buttonRow.Children.Add(_summonButton);
        buttonRow.Children.Add(_settingsButton);
        buttonRow.Children.Add(_closeButton);
    }

    public override void Update()
    {
        if (_closed)
        {
            return;
        }

        if (_component.ChatLogVersion != _renderedVersion)
        {
            _renderedVersion = _component.ChatLogVersion;
            RenderLog();
        }

        _summonButton.Text = _component.IsNpcSummoned ? "收回" : "召唤";
        _statusLabel.Text = _component.IsAgentBusy
            ? "Genius 思考/行动中…"
            : _component.Settings.IsConfigured
                ? $"模型:{_component.Settings.Model}"
                : "未配置 API Key —— 点「设置」";

        if (_sendButton.IsClicked)
        {
            Send();
        }

        if (_summonButton.IsClicked)
        {
            if (_component.IsNpcSummoned)
            {
                _component.DismissNpc();
            }
            else
            {
                _component.SummonNpc();
            }
        }

        if (_settingsButton.IsClicked)
        {
            DialogsManager.ShowDialog(ParentWidget as ContainerWidget, new GeniusSettingsDialog(_component));
        }

        if (_closeButton.IsClicked || Input.Cancel)
        {
            Close();
        }

        // Swallow the rest of this frame's input so HUD widgets that poll the
        // keyboard directly (e.g. the multiplayer chat box on Enter) stay shut
        // while this dialog is open. Our own widgets updated before this call.
        Input.Clear();
    }

    private void Send()
    {
        var text = _inputBox.Text;
        _inputBox.Text = "";
        _inputBox.HasFocus = true;
        _component.SendChat(text);
    }

    private void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        DialogsManager.HideDialog(this);
        _onClosed();
    }

    private void RenderLog()
    {
        var wrapped = new List<(string Text, Color Color)>();
        foreach (var line in _component.ChatLog)
        {
            var (prefix, color) = line.Role switch
            {
                GeniusChatRole.Player => ("你: ", PlayerColor),
                GeniusChatRole.Genius => ("Genius: ", GeniusColor),
                _ => ("· ", InfoColor),
            };
            foreach (var chunk in TextWrapper.Wrap(prefix + line.Text, WrapWidth))
            {
                wrapped.Add((chunk, color));
            }
        }

        var start = Math.Max(0, wrapped.Count - VisibleLines);
        for (var i = 0; i < VisibleLines; i++)
        {
            var index = start + i;
            if (index < wrapped.Count)
            {
                _logLines[i].Text = wrapped[index].Text;
                _logLines[i].Color = wrapped[index].Color;
            }
            else
            {
                _logLines[i].Text = "";
            }
        }
    }

    private static BevelledButtonWidget MakeButton(string text, float width)
    {
        return new BevelledButtonWidget
        {
            Text = text,
            FontScale = 0.7f,
            Size = new Vector2(width, 46f),
            Margin = new Vector2(6f, 0f),
        };
    }
}
