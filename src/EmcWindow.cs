using System;
using System.Collections.Generic;
using UnityEngine;

namespace TransmutationTable
{
    /// <summary>
    /// 转化建筑界面（戴森球计划原生风格）：
    /// 深蓝黑半透明面板 + 细边框 + 青色标题 + 凹槽物品格，使用游戏内字体。
    /// 功能与旧版完全一致：
    ///  - 显示 EMC 余额 / 一键存入背包全部物品
    ///  - 桌：三条传送带端口配置；箱：自动收购 + 输出物品；站：收购/供应端口 + 无限小飞机
    ///  - 输出速度 / 喷涂等级参数
    ///  - 物品格子：左键取出 1 个，Shift+左键取一组，右键取 10 个
    /// </summary>
    public static class EmcWindow
    {
        private static bool _open;
        private static int _tableEntityId;
        private static Rect _rect = new Rect(60, 40, 1140, 820);
        private static Vector2 _scroll;
        private static Vector2 _pickerScroll;
        private static float _measuredScrollTop = 320f; // 上一帧实测的取物区顶部 y（Repaint 阶段测量）
        private static int _pickingLane = -1;
        private static string _hoverText = "";
        private static bool _pendingClose;
        private static bool _dragging; // 手动拖动（不依赖 GUI.DragWindow）
        private static bool _resizing; // 右下角手柄缩放
        private static Vector2 _resizeStartMouse; // 缩放起始鼠标位置（屏幕 GUI 坐标）
        private static Vector2 _resizeStartSize;  // 缩放起始窗口尺寸
        // 点击引起的状态变更延迟到帧末执行：IMGUI 的 Repaint 不重跑 Layout，
        // 当场改变控件数量（开选择器/移除端口等）会触发 "Getting control X's position" 红字
        private static readonly List<Action> _pending = new List<Action>();

        private static void Defer(Action a) => _pending.Add(a);

        public static bool IsOpen => _open;

        public static void Open(int entityId)
        {
            _open = true;
            _tableEntityId = entityId;
            _pickingLane = -1;
        }

        public static void Close()
        {
            _open = false;
            _tableEntityId = 0;
            _pickingLane = -1;
        }

        public static void OnGUIWindow()
        {
            if (!_open) return;
            try
            {
                DrawOnGUI();
            }
            catch (Exception e)
            {
                Diag.Error("EMC窗口", e);
                Close(); // 出错即关窗，避免红字刷屏
            }
        }

        private static void DrawOnGUI()
        {
            if (GameMain.instance == null || GameMain.instance.isMenuDemo ||
                GameMain.data == null || GameMain.data.localPlanet == null)
            {
                Close();
                return;
            }

            // 建筑没了或数据失效 → 关闭
            var factory = GameMain.data.localPlanet.factory;
            if (factory == null || _tableEntityId <= 0 || _tableEntityId >= factory.entityPool.Length ||
                factory.entityPool[_tableEntityId].id != _tableEntityId)
            {
                Close();
                return;
            }
            int winProtoId = factory.entityPool[_tableEntityId].protoId;
            if (winProtoId != TransmutationTablePlugin.TableItemId &&
                winProtoId != TransmutationTablePlugin.BoxItemId &&
                winProtoId != TransmutationTablePlugin.StationItemId)
            {
                Close();
                return;
            }

            if (Event.current.isKey && Event.current.keyCode == KeyCode.Escape && Event.current.type == EventType.KeyDown)
            {
                Close();
                Event.current.Use();
                return;
            }

            EnsureStyles();

            // 手动拖动：按住标题条拖动（MouseDrag 在窗口外也能持续收到，拖动稳定不脱手）
            var evd = Event.current;
            if (_resizing)
            {
                if (evd.type == EventType.MouseDrag && evd.button == 0)
                {
                    _rect.width = Mathf.Clamp(_resizeStartSize.x + (evd.mousePosition.x - _resizeStartMouse.x), 760, Screen.width - 40);
                    _rect.height = Mathf.Clamp(_resizeStartSize.y + (evd.mousePosition.y - _resizeStartMouse.y), 600, Screen.height - 60);
                    evd.Use();
                }
                else if (evd.type == EventType.MouseUp && evd.button == 0)
                {
                    _resizing = false;
                    evd.Use();
                }
            }
            else if (_dragging)
            {
                if (evd.type == EventType.MouseDrag && evd.button == 0)
                {
                    _rect.x += evd.delta.x;
                    _rect.y += evd.delta.y;
                    evd.Use();
                }
                else if (evd.type == EventType.MouseUp && evd.button == 0)
                {
                    _dragging = false;
                    evd.Use();
                }
            }

            _rect = GUI.Window(0x7E6A, _rect, DrawWindow, "", _window); // 固定尺寸窗口，杜绝自动量高的反馈增长
        }

        // ============================ 调色板（DSP 风格） ============================
        private static Color ColPanel = new Color(0.043f, 0.075f, 0.110f, 0.96f);   // 深蓝黑面板
        private static Color ColPanelHeader = new Color(0.090f, 0.150f, 0.210f, 1f); // 标题条
        private static Color ColBorder = new Color(0.275f, 0.415f, 0.545f, 1f);      // 边框蓝灰
        private static Color ColBorderDim = new Color(0.165f, 0.255f, 0.340f, 1f);
        private static Color ColSlot = new Color(0.024f, 0.043f, 0.063f, 1f);        // 凹槽
        private static Color ColSlotBorder = new Color(0.220f, 0.330f, 0.430f, 1f);
        private static Color ColCyan = new Color(0.420f, 0.830f, 1f);                // 标题青
        private static Color ColText = new Color(0.870f, 0.920f, 0.950f);
        private static Color ColDim = new Color(0.545f, 0.655f, 0.730f);
        private static Color ColGold = new Color(1f, 0.850f, 0.380f);                // EMC 数值金
        private static Color ColGreen = new Color(0.470f, 0.930f, 0.550f);
        private static Color ColRed = new Color(1f, 0.420f, 0.360f);
        private static Color ColBtn = new Color(0.098f, 0.165f, 0.235f, 1f);
        private static Color ColBtnHover = new Color(0.160f, 0.290f, 0.400f, 1f);
        private static Color ColSection = new Color(0.330f, 0.530f, 0.680f, 1f);

        private static GUIStyle _window, _title, _stat, _label, _labelDim, _btn, _btnSmall, _priceOk, _priceNo,
            _section, _cellText;
        private static Texture2D _texWin, _texHeader, _texBtn, _texBtnHover, _texSlot, _texFlat, _texDivider;
        private static Font _gameFont;

        private static bool _stylesReady;

        private static Texture2D MakeTex(int w, int h, Color fill, Color border, int borderPx)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.hideFlags = HideFlags.HideAndDontSave;
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool isBorder = x < borderPx || y < borderPx || x >= w - borderPx || y >= h - borderPx;
                    px[y * w + x] = isBorder ? border : fill;
                }
            }
            t.SetPixels(px);
            t.Apply(false, false);
            return t;
        }

        private static Texture2D MakeFlat(Color c)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            t.hideFlags = HideFlags.HideAndDontSave;
            t.SetPixels(new[] { c, c, c, c });
            t.Apply(false, false);
            return t;
        }

        private static Font GameFont()
        {
            if (_gameFont != null) return _gameFont;
            try
            {
                // 借用游戏 UI 的字体，保证观感一致
                _gameFont = UIRoot.instance?.uiGame?.inventoryWindow?.titleText?.font;
            }
            catch { }
            if (_gameFont == null)
                _gameFont = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 14);
            return _gameFont;
        }

        private static void EnsureStyles()
        {
            if (_stylesReady) return;
            var font = GameFont();

            _texWin = MakeTex(24, 24, ColPanel, ColBorder, 2);
            _texHeader = MakeFlat(ColPanelHeader);
            _texBtn = MakeTex(12, 12, ColBtn, ColBorderDim, 1);
            _texBtnHover = MakeTex(12, 12, ColBtnHover, ColCyan, 1);
            _texSlot = MakeTex(12, 12, ColSlot, ColSlotBorder, 1);
            _texFlat = MakeFlat(new Color(1, 1, 1, 1));
            _texDivider = MakeFlat(new Color(ColBorder.r, ColBorder.g, ColBorder.b, 0.55f));

            _window = new GUIStyle
            {
                border = new RectOffset(6, 6, 6, 6),
                padding = new RectOffset(24, 24, 16, 18),
                font = font
            };
            _window.normal.background = _texWin;

            _title = new GUIStyle
            {
                fontSize = 32,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                font = font
            };
            _title.normal.textColor = ColCyan;

            _stat = new GUIStyle
            {
                fontSize = 30,
                alignment = TextAnchor.MiddleLeft,
                font = font
            };
            _stat.normal.textColor = ColGold;

            _label = new GUIStyle
            {
                fontSize = 24,
                alignment = TextAnchor.MiddleLeft,
                font = font,
                wordWrap = true
            };
            _label.normal.textColor = ColText;

            _labelDim = new GUIStyle(_label);
            _labelDim.fontSize = 20;
            _labelDim.normal.textColor = ColDim;

            _section = new GUIStyle
            {
                fontSize = 26,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                font = font
            };
            _section.normal.textColor = ColSection;

            _btn = new GUIStyle
            {
                fontSize = 24,
                alignment = TextAnchor.MiddleCenter,
                font = font,
                border = new RectOffset(3, 3, 3, 3),
                padding = new RectOffset(8, 8, 4, 4)
            };
            _btn.normal.background = _texBtn;
            _btn.normal.textColor = ColText;
            _btn.hover.background = _texBtnHover;
            _btn.hover.textColor = Color.white;
            _btn.active.background = _texBtnHover;
            _btn.active.textColor = ColCyan;

            _btnSmall = new GUIStyle(_btn);
            _btnSmall.fontSize = 22;
            _btnSmall.padding = new RectOffset(6, 6, 3, 3);

            _priceOk = new GUIStyle
            {
                fontSize = 17,
                alignment = TextAnchor.LowerRight,
                font = font
            };
            _priceOk.normal.textColor = ColGreen;
            _priceNo = new GUIStyle(_priceOk);
            _priceNo.normal.textColor = ColRed;

            _cellText = new GUIStyle
            {
                fontSize = 22,
                alignment = TextAnchor.MiddleLeft,
                font = font
            };
            _cellText.normal.textColor = ColText;

            _stylesReady = true;
        }

        // ============================ 绘制辅助 ============================
        private static void DrawFlat(Rect r, Texture2D tex)
        {
            if (r.width > 0 && r.height > 0) GUI.DrawTexture(r, tex, ScaleMode.StretchToFill);
        }

        private static void DrawDivider(float width)
        {
            var r = GUILayoutUtility.GetRect(width, 2);
            DrawFlat(r, _texDivider);
        }

        private static void DrawSection(string text)
        {
            GUILayout.Space(6);
            GUILayout.Label(text, _section, GUILayout.Height(32));
            DrawDivider(_rect.width - 28);
            GUILayout.Space(2);
        }

        private static void Hover(string text)
        {
            if (!string.IsNullOrEmpty(text)) _hoverText = text;
        }

        // ============================ 主窗口 ============================
        private static void DrawWindow(int id)
        {
            _hoverText = "";
            Event ev = Event.current;

            // 内容区域强制等于窗口高度：取物滚动视图的 ExpandHeight 才能撑满下方空间
            GUILayout.BeginArea(new Rect(0, 0, _rect.width, _rect.height));

            PlanetFactory factory0 = GameMain.data?.localPlanet?.factory;
            string title = "转 化 桌";
            if (factory0 != null && _tableEntityId > 0 && _tableEntityId < factory0.entityPool.Length)
            {
                int pid = factory0.entityPool[_tableEntityId].protoId;
                if (pid == TransmutationTablePlugin.BoxItemId) title = "转 化 箱";
                else if (pid == TransmutationTablePlugin.StationItemId) title = "转 化 站";
            }

            // ---- 标题条 ----
            var headerRect = GUILayoutUtility.GetRect(_rect.width - 48, 62);
            DrawFlat(headerRect, _texHeader);
            var proto0 = factory0 != null && _tableEntityId > 0 && _tableEntityId < factory0.entityPool.Length
                ? LDB.items.Select(factory0.entityPool[_tableEntityId].protoId) : null;
            if (proto0 != null)
                DrawIcon(proto0, headerRect.x, headerRect.y + 9, 44, 44);
            var titleR = new Rect(headerRect.x + 58, headerRect.y + 10, headerRect.width - 140, 44);
            GUI.Label(titleR, title, _title);

            var closeR = new Rect(headerRect.x + headerRect.width - 54, headerRect.y + 11, 44, 40);
            bool closeHover = closeR.Contains(ev.mousePosition);
            DrawFlat(closeR, closeHover ? _texBtnHover : _texBtn);
            GUI.Label(new Rect(closeR.x, closeR.y + 2, closeR.width, closeR.height), "✕",
                new GUIStyle(_labelDim) { alignment = TextAnchor.MiddleCenter, fontSize = 24 });
            if (ev.type == EventType.MouseDown && closeR.Contains(ev.mousePosition) && ev.button == 0)
            {
                ev.Use();
                _pendingClose = true; // 延迟到本帧绘制完毕后关闭，避免 Layout/Repaint 控制数不一致
            }
            else if (ev.type == EventType.MouseDown && ev.button == 0 &&
                     ev.mousePosition.y <= headerRect.y + headerRect.height)
            {
                // 标题条区域按下 → 开始手动拖动（关闭按钮除外）
                _dragging = true;
                ev.Use();
            }

            // ---- EMC 储量条 ----
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Label("EMC 储量", _labelDim, GUILayout.Width(120));
            GUILayout.Label(EmcSystem.Balance.ToString("N1"), _stat, GUILayout.Height(40));
            GUILayout.FlexibleSpace();
            GUILayout.Label($"全宇宙共享", _labelDim);
            GUILayout.EndHorizontal();
            DrawDivider(_rect.width - 28);

            // ---- 存入背包 ----
            GUILayout.Space(6);
            var storeRect = GUILayoutUtility.GetRect(_rect.width - 48, 54);
            bool storeHover = storeRect.Contains(ev.mousePosition);
            DrawFlat(storeRect, storeHover ? _texBtnHover : _texBtn);
            GUI.Label(new Rect(storeRect.x, storeRect.y + 10, storeRect.width, 36), "⇦  存入背包中全部物品（按 EMC 折算）",
                new GUIStyle(_cellText) { alignment = TextAnchor.MiddleCenter });
            if (ev.type == EventType.MouseDown && storeRect.Contains(ev.mousePosition) && ev.button == 0)
            {
                ev.Use();
                StoreAllFromPackage();
            }

            // ---- 学习进度 / 沙盒一键解锁 ----
            GUILayout.Space(4);
            if (GameMain.sandboxToolsEnabled)
            {
                var unlockRect = GUILayoutUtility.GetRect(_rect.width - 48, 44);
                bool unlockHover = unlockRect.Contains(ev.mousePosition);
                DrawFlat(unlockRect, unlockHover ? _texBtnHover : _texBtn);
                GUI.Label(new Rect(unlockRect.x, unlockRect.y + 6, unlockRect.width, 32),
                    $"一键解锁全部物品（沙盒）　当前已学习 {EmcSystem.LearnedCount} 种",
                    new GUIStyle(_cellText) { alignment = TextAnchor.MiddleCenter });
                if (ev.type == EventType.MouseDown && unlockRect.Contains(ev.mousePosition) && ev.button == 0)
                {
                    ev.Use();
                    Defer(() =>
                    {
                        int added = EmcSystem.LearnAll();
                        UIRealtimeTip.Popup(added > 0 ? $"已解锁 {added} 种物品，现在全部可兑换" : "所有物品都已解锁");
                    });
                }
            }
            else
            {
                GUILayout.Label($"已学习 {EmcSystem.LearnedCount} 种物品：投入过一次的物品才能兑换（沙盒可一键解锁）", _labelDim,
                    GUILayout.Height(26));
            }

            // ---- 建筑类型判定与配置区 ----
            PlanetFactory factory = GameMain.data?.localPlanet?.factory;
            FractionatorComponent frac = default;
            bool hasBinding = factory != null && TablePatches.TryGetTableBinding(factory, _tableEntityId, out frac);
            long configKey = factory != null ? EmcSystem.MakeKey(factory.planet.id, _tableEntityId) : 0;
            int selfId = factory != null ? factory.entityPool[_tableEntityId].protoId : 0;
            bool isBox = selfId == TransmutationTablePlugin.BoxItemId;
            bool isStation = selfId == TransmutationTablePlugin.StationItemId;

            if (isBox)
            {
                DrawSection("箱体配置");
                GUILayout.Label("箱内物品自动收购为 EMC：用分拣器送入即可，无需配置。", _labelDim);
                var st = GetBoxStorage(factory);
                if (st != null)
                {
                    int totalCount = 0;
                    for (int g = 0; g < st.size && g < st.grids.Length; g++) totalCount += st.grids[g].count;
                    GUILayout.Label($"箱内剩余：{totalCount} 个物品（分拣器送入的物品会被立即收购）", _label);
                }

                int outItem = EmcSystem.GetBoxOutput(configKey);
                GUILayout.Space(2);
                GUILayout.BeginHorizontal();
                GUILayout.Label("输出物品:", _label, GUILayout.Width(60));
                if (outItem > 0)
                {
                    var proto = LDB.items.Select(outItem);
                    Hover("箱内自动备货 1 组，分拣器取走即补");
                    GUILayout.Label($"{proto?.name ?? outItem.ToString()}　{EmcSystem.EmcOf(outItem):N1}/个", _label);
                }
                else
                {
                    GUILayout.Label("<未配置>", _labelDim);
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("选择", _btnSmall, GUILayout.Width(90)))
                    Defer(() => _pickingLane = _pickingLane == 200 ? -1 : 200);
                if (outItem > 0 && GUILayout.Button("停止", _btnSmall, GUILayout.Width(90)))
                    Defer(() => EmcSystem.SetBoxOutput(configKey, 0));
                GUILayout.EndHorizontal();
                DrawBuildParams(configKey);
            }
            else if (isStation)
            {
                var stationPool = factory?.transport?.stationPool;
                int stationId = factory != null ? factory.entityPool[_tableEntityId].stationId : 0;
                StationComponent st = (stationPool != null && stationId > 0 && stationId < stationPool.Length)
                    ? stationPool[stationId] : null;
                if (st == null)
                {
                    GUILayout.Label("<站点组件未就绪>", _labelDim);
                }
                else
                {
                    var cfg = EmcSystem.GetStationCfg(configKey);

                    DrawSection("输入端口（自动收购）");
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(cfg.AutoBuy ? "● 收购中" : "○ 已关闭", _label,
                        GUILayout.Width(300));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(cfg.AutoBuy ? "关闭收购" : "开启收购", _btnSmall, GUILayout.Width(190)))
                        Defer(() => cfg.AutoBuy = !cfg.AutoBuy);
                    GUILayout.EndHorizontal();
                    if (cfg.AutoBuy && st.storage != null)
                    {
                        var names = new System.Text.StringBuilder();
                        for (int s = 0; s < st.storage.Length; s++)
                            if (st.storage[s].itemId > 0 && st.storage[s].localLogic == ELogisticStorage.Demand)
                                names.Append(LDB.items.Select(st.storage[s].itemId)?.name ?? st.storage[s].itemId.ToString()).Append("、");
                        GUILayout.Label(names.Length == 0
                            ? "　等待星球上有塔供应物品…"
                            : "　收购清单：" + names.ToString(0, names.Length - 1), _labelDim);
                    }

                    DrawSection("输出端口");
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(cfg.AutoSupply ? "● 自动满足需求" : "○ 手动供应", _label, GUILayout.Width(320));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(cfg.AutoSupply ? "切换为手动" : "切换为自动", _btnSmall, GUILayout.Width(230)))
                        Defer(() => cfg.AutoSupply = !cfg.AutoSupply);
                    GUILayout.EndHorizontal();

                    if (cfg.AutoSupply && st.storage != null)
                    {
                        var names = new System.Text.StringBuilder();
                        for (int s = 0; s < st.storage.Length; s++)
                            if (st.storage[s].itemId > 0 && st.storage[s].localLogic == ELogisticStorage.Supply)
                                names.Append(LDB.items.Select(st.storage[s].itemId)?.name ?? st.storage[s].itemId.ToString()).Append("、");
                        GUILayout.Label(names.Length == 0
                            ? "　等待星球上有塔发出需求…"
                            : "　供应清单：" + names.ToString(0, names.Length - 1), _labelDim);
                    }

                    for (int k = 0; k < cfg.Outputs.Count; k++)
                    {
                        var proto = LDB.items.Select(cfg.Outputs[k]);
                        string priceNote = EmcSystem.HasEmc(cfg.Outputs[k])
                            ? $"{EmcSystem.EmcOf(cfg.Outputs[k]):N1}/个"
                            : "(临时价1/个)";
                        GUILayout.BeginHorizontal();
                        GUILayout.Label($"　端口{k + 1}", _labelDim, GUILayout.Width(100));
                        GUILayout.Label($"{proto?.name ?? cfg.Outputs[k].ToString()}", _label);
                        GUILayout.Label(priceNote, _priceOk, GUILayout.Width(160));
                        int idx = k;
                        if (GUILayout.Button("移除", _btnSmall, GUILayout.Width(90)))
                            Defer(() => cfg.Outputs.RemoveAt(idx));
                        GUILayout.EndHorizontal();
                    }
                    if (cfg.Outputs.Count < EmcSystem.StationSlotCount)
                    {
                        if (GUILayout.Button("＋ 添加输出端口", _btn, GUILayout.Height(48)))
                            Defer(() => _pickingLane = _pickingLane == 300 ? -1 : 300);
                    }

                    DrawBuildParams(configKey);

                    DrawSection("无人机");
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("无限小飞机:", _label, GUILayout.Width(170));
                    if (GUILayout.Button(cfg.InfiniteDrones ? "✓ 开（机队秒回满）" : "✗ 关（普通模式）", _btn, GUILayout.Width(330)))
                        cfg.InfiniteDrones = !cfg.InfiniteDrones;
                    GUILayout.Label($"当前 {st.workDroneCount + st.idleDroneCount}/{st.workDroneDatas?.Length ?? 0} 架", _labelDim);
                    GUILayout.EndHorizontal();
                    GUILayout.Label("　开：无人机被打掉也立刻补满（无限）　关：送完货自动回站，仅被摧毁才减少，拆除不返还", _labelDim);
                    GUILayout.Label($"本级流水：供应支出 {TablePatches.StationSupplySpent:N0} / 收购收入 {TablePatches.StationBuyEarned:N0} EMC（本次游戏累计）", _labelDim);
                }
            }
            else
            {
                DrawSection("传送带端口");
                for (int slot = 0; slot < 3; slot++)
                {
                    int belt = slot == 0 ? frac.belt0 : slot == 1 ? frac.belt1 : frac.belt2;
                    bool isOutput = slot == 0 ? frac.isOutput0 : slot == 1 ? frac.isOutput1 : frac.isOutput2;
                    bool connected = hasBinding && belt > 0 && belt < factory.cargoTraffic.beltPool.Length &&
                                     factory.cargoTraffic.beltPool[belt].id == belt;

                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"端口{slot + 1}", _label, GUILayout.Width(100));

                    if (!connected)
                    {
                        GUILayout.Label("<未连接传送带>", _labelDim);
                    }
                    else if (!isOutput)
                    {
                        Hover("输入带：到达物品自动收购为 EMC");
                        GUILayout.Label($"输入带 #{belt}（自动收购）", _labelDim);
                    }
                    else
                    {
                        GUILayout.Label($"输出带 #{belt}", _label, GUILayout.Width(170));
                        int item = EmcSystem.GetOutput(configKey, slot);
                        if (item > 0)
                        {
                            var proto = LDB.items.Select(item);
                            Hover($"{proto?.name} — {EmcSystem.EmcOf(item):N1} EMC/个");
                            GUILayout.Label($"{proto?.name ?? item.ToString()}", _label);
                            GUILayout.Label($"{EmcSystem.EmcOf(item):N1}/个", _priceOk, GUILayout.Width(160));
                        }
                        else
                        {
                            GUILayout.Label("<未配置输出物品>", _labelDim);
                        }
                        GUILayout.FlexibleSpace();
                        int slotIdx = slot;
                        if (GUILayout.Button("选择", _btnSmall, GUILayout.Width(90)))
                            Defer(() => _pickingLane = _pickingLane == slotIdx ? -1 : slotIdx);
                        if (item > 0 && GUILayout.Button("停止", _btnSmall, GUILayout.Width(90)))
                            Defer(() => EmcSystem.SetOutput(configKey, slotIdx, 0));
                    }
                    GUILayout.EndHorizontal();
                }
                DrawBuildParams(configKey);
                GUILayout.Label("提示：输出带 = 从端口往外拉的带；输入带 = 甩头插进端口、箭头指向桌子的带。配置每栋建筑独立。", _labelDim);
            }

            GUILayout.Space(4);
            GUILayout.Label($"tick={TablePatches.TickCount}　收购={TablePatches.PickedTotal}　输出成功={TablePatches.InsertOkTotal}　带满={TablePatches.InsertFailTotal}　EMC不足={TablePatches.NoEmcSkipTotal}", _labelDim);

            // ---- 物品选择器 ----
            if (_pickingLane >= 0)
            {
                DrawSection("选择物品");
                string label;
                Action<int> onPick;
                if (_pickingLane >= 300)
                {
                    label = "为转化站添加输出端口（供应物品）：";
                    onPick = protoId =>
                    {
                        var cfg = EmcSystem.GetStationCfg(configKey);
                        if (!cfg.Outputs.Contains(protoId) && cfg.Outputs.Count < EmcSystem.StationSlotCount)
                            cfg.Outputs.Add(protoId);
                    };
                }
                else if (_pickingLane >= 200)
                {
                    label = "为转化箱选择输出物品：";
                    onPick = protoId => EmcSystem.SetBoxOutput(configKey, protoId);
                }
                else
                {
                    label = $"为端口{_pickingLane + 1}选择输出物品：";
                    onPick = protoId => EmcSystem.SetOutput(configKey, _pickingLane, protoId);
                }
                GUILayout.Label(label, _label);
                _pickerScroll = GUILayout.BeginScrollView(_pickerScroll, GUILayout.Height(260));
                DrawItemGrid(EmcSystem.SortedItems, protoId => Defer(() =>
                {
                    onPick(protoId);
                    _pickingLane = -1;
                }), true);
                GUILayout.EndScrollView();
                GUILayout.Space(4);
            }

            // ---- 取物区 ----
            DrawSection("从 EMC 取出到背包");
            GUILayout.Label("左键 ×1　Shift+左键 ×1组　右键 ×10　｜　未学习的物品需先投入一次", _labelDim);
            // 实测顶部位置（GetLastRect 仅 Repaint 有效，缓存供下一帧 Layout 使用）
            var lastRect = GUILayoutUtility.GetLastRect();
            if (ev.type == EventType.Repaint && lastRect.yMax > 1f)
                _measuredScrollTop = lastRect.yMax;
            float scrollH = Mathf.Clamp(_rect.height - _measuredScrollTop - 78f, 150f, 1400f);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(scrollH));
            DrawItemGrid(EmcSystem.SortedItems, null, false);
            GUILayout.EndScrollView();

            // 悬停信息条（常驻占位：保证每帧控件数量一致，避免 Layout/Repaint 不匹配）
            GUILayout.Space(2);
            GUILayout.Label(_hoverText ?? "", _labelDim, GUILayout.Height(30));

            // 缩放手柄（右下角）
            var grip = new Rect(_rect.width - 40, _rect.height - 40, 40, 40);
            bool gripHover = grip.Contains(ev.mousePosition) || _resizing;
            DrawFlat(grip, gripHover ? _texBtnHover : _texSlot);
            GUI.Label(new Rect(grip.x + 4, grip.y + 2, 32, 34), "◢",
                new GUIStyle(_labelDim) { fontSize = 22, alignment = TextAnchor.MiddleCenter });
            if (ev.type == EventType.MouseDown && grip.Contains(ev.mousePosition) && ev.button == 0)
            {
                _resizing = true;
                _resizeStartMouse = GUIUtility.GUIToScreenPoint(ev.mousePosition);
                _resizeStartSize = new Vector2(_rect.width, _rect.height);
                ev.Use();
            }

            GUILayout.EndArea();

            // 帧末执行延迟的状态变更（此时本帧 Layout/Repaint 已结束）
            if (_pending.Count > 0)
            {
                var acts = new List<Action>(_pending);
                _pending.Clear();
                foreach (var a in acts) a();
            }

            if (_pendingClose)
            {
                _pendingClose = false;
                Close();
            }
        }

        private static StorageComponent GetBoxStorage(PlanetFactory factory)
        {
            return factory != null && factory.entityPool[_tableEntityId].storageId > 0
                ? factory.factoryStorage.storagePool[factory.entityPool[_tableEntityId].storageId]
                : null;
        }

        /// <summary>建筑附加参数行：输出速度（个/秒）+ 输出喷涂等级（桌/箱/站通用）</summary>
        private static void DrawBuildParams(long configKey)
        {
            var bcfg = EmcSystem.GetBuildCfg(configKey);
            DrawSection("输出参数");
            GUILayout.BeginHorizontal();
            GUILayout.Label("输出速度:", _label, GUILayout.Width(120));
            if (GUILayout.Button(bcfg.OutputSpeed + " 个/秒", _btn, GUILayout.Width(190), GUILayout.Height(46)))
            {
                int[] presets = { 1, 2, 5, 10, 20, 30, 60 };
                int idx = System.Array.IndexOf(presets, bcfg.OutputSpeed);
                bcfg.OutputSpeed = presets[(idx + 1) % presets.Length];
            }
            GUILayout.Space(20);
            GUILayout.Label("喷涂:", _label, GUILayout.Width(80));
            string[] sprayNames = { "无", "MK.I", "MK.II", "MK.III" };
            if (GUILayout.Button(sprayNames[bcfg.SprayLevel], _btn, GUILayout.Width(140), GUILayout.Height(46)))
                Defer(() => bcfg.SprayLevel = (bcfg.SprayLevel + 1) % 4);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (bcfg.SprayLevel > 0)
            {
                double extra = EmcSystem.SprayCostPerItem(bcfg.SprayLevel);
                int hpMax = LDB.items.Select(new[] { 1141, 1142, 1143 }[bcfg.SprayLevel - 1]).HpMax;
                Hover($"涂料价÷可喷{hpMax}次，带增产能效果");
                GUILayout.Label($"　喷涂加价：每件 +{extra:N3} EMC（涂料价÷可喷{hpMax}次），带增产能效果", _labelDim);
            }
        }

        // ---------------- 物品网格 ----------------
        private const float CellSize = 76f;

        private static void DrawItemGrid(IReadOnlyList<ItemProto> items, Action<int> onClick, bool pickerMode)
        {
            if (items == null || items.Count == 0)
            {
                GUILayout.Label("（价值表尚未构建完成）", _labelDim);
                return;
            }

            // 选择器只列已学习物品：未学习的物品不能配置为输出
            IReadOnlyList<ItemProto> view = items;
            if (pickerMode)
            {
                var learnedOnly = new List<ItemProto>(items.Count);
                for (int i = 0; i < items.Count; i++)
                    if (EmcSystem.IsLearned(items[i].ID)) learnedOnly.Add(items[i]);
                if (learnedOnly.Count == 0)
                {
                    GUILayout.Label("尚无已学习物品：先把物品投入转化设施，学过的物品才能作为输出", _labelDim);
                    return;
                }
                view = learnedOnly;
            }

            Event ev = Event.current;
            float width = _rect.width - 28;
            int perRow = Mathf.Max(3, (int)(width / (CellSize + 4)));
            int rows = Mathf.CeilToInt(view.Count / (float)perRow);
            Rect gridRect = GUILayoutUtility.GetRect(width, rows * (CellSize + 3));

            for (int i = 0; i < view.Count; i++)
            {
                var p = view[i];
                int row = i / perRow, col = i % perRow;
                var cell = new Rect(gridRect.x + col * (CellSize + 3), gridRect.y + row * (CellSize + 3), CellSize, CellSize);
                bool hovered = cell.Contains(ev.mousePosition);

                bool learned = EmcSystem.IsLearned(p.ID);
                double emc = EmcSystem.EmcOf(p.ID);
                bool affordable = EmcSystem.Balance + 1e-9 >= emc;

                DrawFlat(cell, _texSlot);
                DrawIcon(p, cell.x + 3, cell.y + 3, CellSize - 6, CellSize - 6);

                if (!learned)
                {
                    // 未学习：压暗 + 中央标注（投入一次即可解锁）
                    var prevColor = GUI.color;
                    GUI.color = new Color(0.02f, 0.03f, 0.05f, 0.72f);
                    GUI.DrawTexture(new Rect(cell.x + 1, cell.y + 1, CellSize - 2, CellSize - 2), _texSlot, ScaleMode.StretchToFill);
                    GUI.color = prevColor;
                    GUI.Label(new Rect(cell.x + 2, cell.y + CellSize / 2 - 9, CellSize - 4, 18),
                        "未学习", new GUIStyle(_labelDim) { alignment = TextAnchor.MiddleCenter, fontSize = 15 });
                }
                else if (!pickerMode)
                {
                    GUI.Label(new Rect(cell.x + 2, cell.y + CellSize - 15, CellSize - 4, 13),
                        FormatEmc(emc), affordable ? _priceOk : _priceNo);
                }

                if (hovered)
                {
                    // DSP 风格悬停：青色描边 + 底部信息条
                    DrawFlat(new Rect(cell.x - 1, cell.y - 1, CellSize + 2, 1), _texFlat.SetColor(learned ? ColCyan : ColBorderDim));
                    DrawFlat(new Rect(cell.x - 1, cell.y + CellSize, CellSize + 2, 1), _texFlat.SetColor(learned ? ColCyan : ColBorderDim));
                    DrawFlat(new Rect(cell.x - 1, cell.y, 1, CellSize), _texFlat.SetColor(learned ? ColCyan : ColBorderDim));
                    DrawFlat(new Rect(cell.x + CellSize, cell.y, 1, CellSize), _texFlat.SetColor(learned ? ColCyan : ColBorderDim));
                    if (!learned)
                        Hover($"{p.name}　未学习 —— 把该物品投入一次转化设施即可解锁兑换");
                    else if (pickerMode)
                        Hover($"{p.name}　{emc:N1} EMC —— 点击选择");
                    else
                        Hover($"{p.name}　{emc:N1} EMC —— 左键×1 / Shift+左键×1组 / 右键×10" +
                              (affordable ? "" : "　(EMC 不足)"));
                }

                // 点击处理
                if (ev.type == EventType.MouseDown && cell.Contains(ev.mousePosition) &&
                    ev.button >= 0 && ev.button <= 1)
                {
                    ev.Use();
                    if (!learned) return; // 未学习不可操作
                    if (pickerMode)
                    {
                        onClick?.Invoke(p.ID);
                    }
                    else
                    {
                        if (ev.button == 0) Withdraw(p.ID, ev.shift ? GetStackSize(p.ID) : 1);
                        else if (ev.button == 1) Withdraw(p.ID, 10);
                    }
                }
            }
        }

        private static void Withdraw(int itemId, int count)
        {
            if (count <= 0 || !EmcSystem.IsLearned(itemId)) return;
            double unit = EmcSystem.EmcOf(itemId);
            int affordable = (int)Math.Floor((EmcSystem.Balance + 1e-9) / unit);
            int give = Math.Min(count, affordable);
            if (give <= 0) return;

            // 统一取出到玩家背包（转化箱会秒回收箱内物品，取到箱内无意义）
            var package = GameMain.mainPlayer.package;
            int added = package.AddItem(itemId, give, 0, out int _, false);
            if (added > 0)
                EmcSystem.Balance -= unit * added;
        }

        private static int GetStackSize(int itemId)
        {
            var p = LDB.items.Select(itemId);
            return p?.StackSize ?? 1;
        }

        private static void StoreAllFromPackage()
        {
            var package = GameMain.mainPlayer.package;
            if (package == null || package.grids == null) return;
            double gained = 0;
            for (int i = 0; i < package.size && i < package.grids.Length; i++)
            {
                if (package.grids[i].itemId <= 0 || package.grids[i].count <= 0) continue;
                int itemId = package.grids[i].itemId;
                int tmpId = 0, tmpCount = 0, inc;
                package.TakeItemFromGrid(i, ref tmpId, ref tmpCount, out inc);
                if (tmpId == itemId && tmpCount > 0)
                {
                    EmcSystem.Learn(itemId);
                    gained += EmcSystem.EmcOf(itemId) * tmpCount;
                }
            }
            if (gained > 0)
                EmcSystem.Balance += gained;
        }

        // ---------------- 工具 ----------------
        private static void DrawIcon(ItemProto proto, float x, float y, float w, float h)
        {
            if (proto == null) return;
            var sprite = proto.iconSprite;
            if (sprite == null) return;
            Rect texRect = sprite.textureRect;
            var coords = new Rect(texRect.x / sprite.texture.width, texRect.y / sprite.texture.height,
                texRect.width / sprite.texture.width, texRect.height / sprite.texture.height);
            GUI.DrawTextureWithTexCoords(new Rect(x, y, w, h), sprite.texture, coords, true);
        }

        private static string FormatEmc(double v)
        {
            if (v >= 10000) return (v / 1000).ToString("0.#") + "k";
            if (v >= 100) return ((int)v).ToString();
            return v.ToString("0.#");
        }
    }

    internal static class GuiTexExt
    {
        // 纹理不持有颜色状态，这里用一次性 2x2 平铺即可；SetColor 返回自身方便链式调用
        public static Texture2D SetColor(this Texture2D t, Color c)
        {
            t.SetPixels(new[] { c, c, c, c });
            t.Apply(false, false);
            return t;
        }
    }
}
