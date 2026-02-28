using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Rage;
using Rage.Native;
using LSPD_First_Response.Mod.API;
using LSPD_First_Response.Mod.Callouts;
using _1803PolicePartner.Models;
using RAGENativeUI;
using RAGENativeUI.Elements;

namespace _1803PolicePartner
{
    public class Main : Plugin
    {
        private static List<Partner> _activePartners = new List<Partner>();
        private static bool _isInitialized = false;
        private static DateTime _lastYPress = DateTime.MinValue;

        // Menu key debouncing
        private static bool _menuKeyPressed = false;
        private static DateTime _lastMenuKeyPress = DateTime.MinValue;
        private static readonly int MenuKeyDebounceMs = 300;

        // Menu system
        private MenuPool _menuPool;
        private UIMenu _mainMenu;
        private UIMenu _spawnMenu;
        private UIMenu _manageMenu;

        // Keybinds
        private static readonly Keys MenuKey = Keys.F2;
        private static readonly Keys CallBackupKey = Keys.Y;

        public override void Initialize()
        {
            Game.LogTrivial("1803 Police Partner - Version 1.0.0 - Initializing...");

            _isInitialized = true;

            // Setup menus
            SetupMenus();

            // Register events
            GameFiber.StartNew(delegate
            {
                while (_isInitialized)
                {
                    GameFiber.Sleep(10);
                    ProcessKeybinds();
                    UpdatePartners();
                    _menuPool?.ProcessMenus();
                }
            });

            // Register for LSPDFR events
            Functions.OnOnDutyStateChanged += OnOnDutyStateChanged;

            Game.LogTrivial("1803 Police Partner - Initialized successfully!");
        }

        public override void Finally()
        {
            _isInitialized = false;

            // Clean up partners
            foreach (var partner in Partner.GetAllPartners())
            {
                partner.Dismiss();
            }

            Game.LogTrivial("1803 Police Partner - Shut down");
        }

        private void OnOnDutyStateChanged(bool onDuty)
        {
            if (onDuty)
            {
                Game.DisplayNotification("~b~1803 Police Partner~w~ loaded! Press ~y~F2~w~ to open menu.");
            }
        }

        private void SetupMenus()
        {
            _menuPool = new MenuPool();

            // Main Menu
            _mainMenu = new UIMenu("1803 Police Partner", "MAIN MENU");
            _menuPool.Add(_mainMenu);

            // Spawn Menu
            _spawnMenu = new UIMenu("1803 Police Partner", "SPAWN PARTNER");
            _menuPool.Add(_spawnMenu);

            // Manage Menu
            _manageMenu = new UIMenu("1803 Police Partner", "MANAGE PARTNERS");
            _menuPool.Add(_manageMenu);

            // Main Menu Items
            var spawnItem = new UIMenuItem("Spawn Partner", "Create a new partner unit");
            var manageItem = new UIMenuItem("Manage Partners", "View and manage active partners");
            var removeAllItem = new UIMenuItem("Remove All Partners", "Dismiss all active partners");
            var closeItem = new UIMenuItem("Close Menu", "Exit the menu");

            _mainMenu.AddItem(spawnItem);
            _mainMenu.AddItem(manageItem);
            _mainMenu.AddItem(removeAllItem);
            _mainMenu.AddItem(closeItem);

            // Main Menu Events
            _mainMenu.OnItemSelect += (sender, item, index) =>
            {
                if (item == spawnItem)
                {
                    _mainMenu.Visible = false;
                    RefreshSpawnMenu();
                    _spawnMenu.Visible = true;
                }
                else if (item == manageItem)
                {
                    _mainMenu.Visible = false;
                    RefreshManageMenu();
                    _manageMenu.Visible = true;
                }
                else if (item == removeAllItem)
                {
                    RemoveAllPartners();
                }
                else if (item == closeItem)
                {
                    _mainMenu.Visible = false;
                }
            };

            // Spawn Menu Setup
            BuildSpawnMenu();

            // Manage Menu Setup
            BuildManageMenu();

            // Set menu width
            _mainMenu.WidthOffset = 50;
            _spawnMenu.WidthOffset = 50;
            _manageMenu.WidthOffset = 50;
        }

        private void BuildSpawnMenu()
        {
            _spawnMenu.Clear();

            var policeItem = new UIMenuItem("Police Department", "Spawn a police partner");
            var sheriffItem = new UIMenuItem("Sheriff Department", "Spawn a sheriff partner");
            var highwayItem = new UIMenuItem("Highway Patrol", "Spawn a highway patrol partner");
            var backItem = new UIMenuItem("Back", "Return to main menu");

            _spawnMenu.AddItem(policeItem);
            _spawnMenu.AddItem(sheriffItem);
            _spawnMenu.AddItem(highwayItem);
            _spawnMenu.AddItem(backItem);

            _spawnMenu.OnItemSelect += (sender, item, index) =>
            {
                if (item == policeItem)
                {
                    ShowDepartmentMenu("Police", new List<string> { "LAPD", "NYPD", "SAPD", "LVPD" });
                }
                else if (item == sheriffItem)
                {
                    ShowDepartmentMenu("Sheriff", new List<string> { "LASD", "BCSO", "SCSO", "BSCO" });
                }
                else if (item == highwayItem)
                {
                    ShowDepartmentMenu("Highway Patrol", new List<string> { "CHP", "FHP", "TX-DPS", "ASP" });
                }
                else if (item == backItem)
                {
                    _spawnMenu.Visible = false;
                    _mainMenu.Visible = true;
                }
            };
        }

        private void ShowDepartmentMenu(string agency, List<string> departments)
        {
            var deptMenu = new UIMenu("1803 Police Partner", $"SELECT {agency.ToUpper()} DEPT");
            _menuPool.Add(deptMenu);

            foreach (var dept in departments)
            {
                var deptItem = new UIMenuItem(dept, $"Spawn {dept} {agency}");
                deptMenu.AddItem(deptItem);

                deptItem.Activated += (s, i) =>
                {
                    SpawnPartner(agency, dept);
                    deptMenu.Visible = false;
                    _spawnMenu.Visible = false;
                    _mainMenu.Visible = true;
                };
            }

            var backItem = new UIMenuItem("Back", "Return to spawn menu");
            backItem.Activated += (s, i) =>
            {
                deptMenu.Visible = false;
                _spawnMenu.Visible = true;
            };
            deptMenu.AddItem(backItem);

            _spawnMenu.Visible = false;
            deptMenu.Visible = true;
        }

        private void BuildManageMenu()
        {
            _manageMenu.Clear();

            var partners = Partner.GetAllPartners();

            if (partners.Count == 0)
            {
                var noPartnersItem = new UIMenuItem("No Active Partners", "Spawn a partner first");
                noPartnersItem.Enabled = false;
                _manageMenu.AddItem(noPartnersItem);
            }
            else
            {
                foreach (var partner in partners)
                {
                    if (partner.IsValid())
                    {
                        var partnerItem = new UIMenuItem($"{partner.CallSign} - {partner.Agency}",
                            "Click to dismiss");

                        partnerItem.Activated += (sender, item) =>
                        {
                            RemovePartner(partner);
                            RefreshManageMenu();
                            if (Partner.GetAllPartners().Count == 0)
                            {
                                _manageMenu.Visible = false;
                                _mainMenu.Visible = true;
                            }
                        };

                        _manageMenu.AddItem(partnerItem);
                    }
                }
            }

            var backItem = new UIMenuItem("Back", "Return to main menu");
            backItem.Activated += (sender, item) =>
            {
                _manageMenu.Visible = false;
                _mainMenu.Visible = true;
            };
            _manageMenu.AddItem(backItem);
        }

        private void RefreshSpawnMenu()
        {
            BuildSpawnMenu();
        }

        private void RefreshManageMenu()
        {
            BuildManageMenu();
        }

        private void ProcessKeybinds()
        {
            // Menu key with improved debouncing
            if (Game.IsKeyDown(MenuKey))
            {
                if (!_menuKeyPressed)
                {
                    _menuKeyPressed = true;

                    // Only trigger if enough time has passed since last press
                    if ((DateTime.Now - _lastMenuKeyPress).TotalMilliseconds > MenuKeyDebounceMs)
                    {
                        _lastMenuKeyPress = DateTime.Now;

                        if (_menuPool != null)
                        {
                            // Toggle main menu
                            if (!_menuPool.IsAnyMenuOpen())
                            {
                                _mainMenu.Visible = true;
                            }
                            else
                            {
                                // Close all menus
                                foreach (var menu in _menuPool.ToList())
                                {
                                    menu.Visible = false;
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                _menuKeyPressed = false;
            }

            // Call backup key (Y)
            if (Game.IsKeyDown(CallBackupKey))
            {
                if ((DateTime.Now - _lastYPress).TotalMilliseconds > 500)
                {
                    _lastYPress = DateTime.Now;
                    RequestBackup();
                }
            }
        }

        private void UpdatePartners()
        {
            var partners = Partner.GetAllPartners();

            foreach (var partner in partners)
            {
                partner.Update();
            }
        }

        private void RequestBackup()
        {
            var partners = Partner.GetAllPartners();

            if (partners.Count == 0)
            {
                Game.DisplayNotification("~r~No active partners to respond!");
                return;
            }

            // Check if player is in a traffic stop
            if (Functions.IsPlayerPerformingPullover())
            {
                Game.DisplayNotification("~b~Dispatch~w~: Sending nearest partner to assist with traffic stop.");
                bool responded = Partner.RespondNearestToTrafficStop();

                if (!responded)
                {
                    Game.DisplayNotification("~r~No available partners to respond.");
                }
            }
            // Check if player is on a callout
            else if (Functions.IsCalloutRunning())
            {
                Game.DisplayNotification("~b~Dispatch~w~: Sending nearest partner to assist with callout.");
                bool responded = Partner.RespondNearestToCallout();

                if (!responded)
                {
                    Game.DisplayNotification("~r~No available partners to respond.");
                }
            }
            else
            {
                Game.DisplayNotification("~b~Dispatch~w~: Sending nearest partner to your location.");
                bool responded = Partner.RespondNearestToPursuit();

                if (!responded)
                {
                    Game.DisplayNotification("~r~No available partners to respond.");
                }
            }
        }

        public void SpawnPartner(string agency, string department)
        {
            var partner = new Partner(agency, department, Game.LocalPlayer.Character.Position.Around(20f));
            if (partner.IsValid())
            {
                Game.DisplayNotification($"~g~{partner.CallSign}~w~ has been assigned to you.");
            }
            else
            {
                Game.DisplayNotification($"~r~Failed to spawn {agency} partner. Check RPH log for details.");
            }
        }

        public void RemovePartner(Partner partner)
        {
            if (partner != null)
            {
                partner.Dismiss();
                Game.DisplayNotification($"~r~{partner.CallSign}~w~ has been dismissed.");
            }
        }

        public void RemoveAllPartners()
        {
            foreach (var partner in Partner.GetAllPartners())
            {
                partner.Dismiss();
            }
            Game.DisplayNotification("~r~All partners dismissed.");
        }
    }
}