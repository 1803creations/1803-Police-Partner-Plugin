using System;
using System.Collections.Generic;
using System.Linq;
using Rage;
using Rage.Native;
using LSPD_First_Response.Mod.API;

namespace _1803PolicePartner.Models
{
    public class Partner
    {
        private static int _nextId = 1;
        private static readonly List<Partner> _allPartners = new List<Partner>();

        public int Id { get; private set; }
        public string CallSign { get; private set; }
        public string Agency { get; private set; }
        public string Department { get; private set; }
        public Ped Ped { get; private set; }
        public Vehicle Vehicle { get; private set; }

        public enum PartnerState
        {
            Patrolling,
            RespondingToTrafficStop,
            ApproachingTrafficStop,
            OnTrafficStopScene,
            RespondingToPursuit,
            RespondingToCallout,
            ReturningToPlayer
        }
        private PartnerState _currentState = PartnerState.Patrolling;
        private Vector3 _targetLocation;
        private DateTime _stateStartTime;
        private bool _isDriving = false;
        private int _failedPathAttempts = 0;

        // Traffic stop specific
        private Vehicle _stoppedVehicle = null;
        private Vector3 _originalPosition = Vector3.Zero;
        private bool _hasSetParkingTarget = false;
        private bool _hasExitedVehicle = false;

        // Distance tracking
        private const float MaxDistanceFromPlayer = 804.67f; // 0.5 miles in meters

        // Native driving styles
        private const uint NormalDrivingStyle = 786603;
        private const uint EmergencyDrivingStyle = 1074528293;

        // Agency-specific models
        private static readonly Dictionary<string, Tuple<string, string>> AgencyData = new Dictionary<string, Tuple<string, string>>
        {
            ["Police"] = Tuple.Create("S_M_Y_Cop_01", "POLICE"),
            ["Sheriff"] = Tuple.Create("S_M_Y_Sheriff_01", "SHERIFF"),
            ["Highway Patrol"] = Tuple.Create("S_M_Y_HwayCop_01", "POLICE2")
        };

        public Partner(string agency, string department, Vector3 spawnLocation)
        {
            Id = _nextId++;
            Agency = agency;
            Department = department;

            CallSign = $"{department}-{Id:D3}";

            Game.LogTrivial($"1803 Partner - Attempting to spawn {CallSign}");

            SpawnVehicle(spawnLocation);
            GameFiber.Sleep(100);
            SpawnPedAndPutInVehicle();
            GameFiber.Sleep(100);

            if (Ped && Ped.IsValid() && Vehicle && Vehicle.IsValid())
            {
                Game.LogTrivial($"1803 Partner - {CallSign} spawned successfully in vehicle");

                Blip blip = Ped.AttachBlip();
                blip.Color = System.Drawing.Color.Blue;
                blip.Name = CallSign;

                ConfigurePedForNormalDriving();

                // Add to global list
                lock (_allPartners)
                {
                    _allPartners.Add(this);
                }

                StartPatrolling();
            }
        }

        private void ConfigurePedForNormalDriving()
        {
            if (!Ped || !Ped.IsValid()) return;

            NativeFunction.Natives.SET_DRIVER_AGGRESSIVENESS(Ped, 0.0f);
            NativeFunction.Natives.SET_DRIVER_ABILITY(Ped, 0.5f);

            if (Vehicle && Vehicle.IsValid())
            {
                Vehicle.IsDriveable = true;
            }

            Ped.CanRagdoll = false;
            Ped.KeepTasks = true;
            Ped.BlockPermanentEvents = true;
        }

        private void SpawnVehicle(Vector3 location)
        {
            try
            {
                string vehicleModel = AgencyData[Agency].Item2;
                Model model = new Model(vehicleModel);
                model.LoadAndWait();

                if (!model.IsValid) return;

                Vector3 roadPos;
                if (NativeFunction.Natives.GET_CLOSEST_VEHICLE_NODE<bool>(location.X, location.Y, location.Z, out roadPos, 1, 3f, 0))
                {
                    location = roadPos;
                }

                Vehicle = new Vehicle(model, location, Game.LocalPlayer.Character.Heading);
                Vehicle.IsPersistent = true;

                if (Agency == "Sheriff")
                {
                    Vehicle.PrimaryColor = System.Drawing.Color.Black;
                    Vehicle.SecondaryColor = System.Drawing.Color.White;
                }
                else if (Agency == "Highway Patrol")
                {
                    Vehicle.PrimaryColor = System.Drawing.Color.Black;
                    Vehicle.SecondaryColor = System.Drawing.Color.Gold;
                }
                else
                {
                    Vehicle.PrimaryColor = System.Drawing.Color.Black;
                    Vehicle.SecondaryColor = System.Drawing.Color.White;
                }

                NativeFunction.Natives.SET_VEHICLE_ON_GROUND_PROPERLY(Vehicle);
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"1803 Partner - Failed to spawn vehicle: {ex.Message}");
            }
        }

        private void SpawnPedAndPutInVehicle()
        {
            try
            {
                string pedModel = AgencyData[Agency].Item1;
                Model model = new Model(pedModel);
                model.LoadAndWait();

                if (!model.IsValid) return;

                Vector3 pedSpawnPos = Vehicle.GetOffsetPositionFront(2f);
                Ped = new Ped(model, pedSpawnPos, 0f);

                if (Ped && Ped.IsValid())
                {
                    Ped.IsPersistent = true;
                    Ped.BlockPermanentEvents = true;

                    GameFiber.Sleep(50);
                    Ped.WarpIntoVehicle(Vehicle, 0);
                    Ped.Inventory.GiveNewWeapon("WEAPON_PISTOL", 250, true);
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"1803 Partner - Failed to spawn ped: {ex.Message}");
            }
        }

        public bool IsValid()
        {
            return Ped && Ped.IsValid() && Vehicle && Vehicle.IsValid();
        }

        public void Update()
        {
            if (!IsValid()) return;

            try
            {
                // Check distance from player
                float distanceToPlayer = Ped.DistanceTo(Game.LocalPlayer.Character.Position);

                // If too far away and not responding to something, return to player
                if (distanceToPlayer > MaxDistanceFromPlayer &&
                    _currentState != PartnerState.RespondingToTrafficStop &&
                    _currentState != PartnerState.ApproachingTrafficStop &&
                    _currentState != PartnerState.RespondingToPursuit &&
                    _currentState != PartnerState.RespondingToCallout &&
                    _currentState != PartnerState.ReturningToPlayer &&
                    _currentState != PartnerState.OnTrafficStopScene)
                {
                    Game.LogTrivial($"1803 Partner - {CallSign} is {distanceToPlayer:F0}m from player, returning");
                    _currentState = PartnerState.ReturningToPlayer;
                    DriveToLocation(Game.LocalPlayer.Character.Position, 40f, false);
                    Game.DisplayNotification($"~b~{CallSign}~w~: Returning to your location.");
                }

                // Check if ped is still in vehicle (only if not on scene or approaching)
                if (_currentState != PartnerState.OnTrafficStopScene &&
                    _currentState != PartnerState.ApproachingTrafficStop &&
                    Ped.CurrentVehicle == null)
                {
                    Ped.WarpIntoVehicle(Vehicle, 0);
                }

                // Check if traffic stop ended
                if (_currentState == PartnerState.OnTrafficStopScene || _currentState == PartnerState.ApproachingTrafficStop)
                {
                    if (!Functions.IsPlayerPerformingPullover())
                    {
                        Game.LogTrivial($"1803 Partner - {CallSign} traffic stop ended, returning to patrol");
                        ReturnToPatrolAfterTrafficStop();
                    }
                    else if (_currentState == PartnerState.OnTrafficStopScene)
                    {
                        EnsurePartnerStaysOnScene();
                    }
                }

                // Check if vehicle is stuck
                if (_isDriving && Vehicle.Speed < 0.5f && (DateTime.Now - _stateStartTime).TotalSeconds > 10)
                {
                    _failedPathAttempts++;

                    if (_failedPathAttempts > 3)
                    {
                        _failedPathAttempts = 0;
                        _isDriving = false;
                        _stateStartTime = DateTime.Now.AddSeconds(-30);
                        Ped.Tasks.Clear();
                    }
                    else
                    {
                        if (_currentState == PartnerState.Patrolling)
                            SetRandomPatrolDestination();
                        else if (_currentState == PartnerState.ReturningToPlayer)
                            DriveToLocation(Game.LocalPlayer.Character.Position, 40f, false);
                        else if (_currentState == PartnerState.RespondingToTrafficStop)
                            DriveToLocation(Game.LocalPlayer.Character.Position, 40f, true);
                        else if (_currentState == PartnerState.RespondingToPursuit)
                            DriveToLocation(Game.LocalPlayer.Character.Position, 70f, true);
                        else if (_currentState == PartnerState.RespondingToCallout)
                            DriveToLocation(Game.LocalPlayer.Character.Position, 40f, true);
                    }
                }

                switch (_currentState)
                {
                    case PartnerState.Patrolling:
                        UpdatePatrol();
                        break;
                    case PartnerState.ReturningToPlayer:
                        UpdateResponse();
                        break;
                    case PartnerState.RespondingToTrafficStop:
                        UpdateResponse();
                        break;
                    case PartnerState.ApproachingTrafficStop:
                        UpdateApproach();
                        break;
                    case PartnerState.OnTrafficStopScene:
                        // Already handled above
                        break;
                    case PartnerState.RespondingToPursuit:
                    case PartnerState.RespondingToCallout:
                        UpdateResponse();
                        break;
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"1803 Partner - Update error: {ex.Message}");
            }
        }

        private void EnsurePartnerStaysOnScene()
        {
            if (!IsValid() || _stoppedVehicle == null || !_stoppedVehicle.Exists()) return;

            float distToStop = Ped.DistanceTo(_stoppedVehicle.Position);

            if (distToStop > 15f)
            {
                Game.LogTrivial($"1803 Partner - {CallSign} moving back to scene");
                Vector3 targetPos = _stoppedVehicle.GetOffsetPosition(new Vector3(-4f, -3f, 0f));

                float groundZ;
                if (NativeFunction.Natives.GET_GROUND_Z_FOR_3D_COORD(targetPos.X, targetPos.Y, targetPos.Z + 5f, out groundZ, false))
                {
                    targetPos.Z = groundZ;
                }

                NativeFunction.Natives.TASK_GO_STRAIGHT_TO_COORD(Ped, targetPos.X, targetPos.Y, targetPos.Z, 1.5f, 5000, 0f, 0.5f);
            }
        }

        private void ReturnToPatrolAfterTrafficStop()
        {
            if (!IsValid()) return;

            _currentState = PartnerState.Patrolling;
            _stoppedVehicle = null;
            _hasSetParkingTarget = false;
            _hasExitedVehicle = false;

            if (Ped.CurrentVehicle == null)
            {
                Ped.Tasks.EnterVehicle(Vehicle, 5000, -1);
                GameFiber.Sleep(3000);
            }

            if (Vehicle && Vehicle.IsValid())
            {
                Vehicle.IsSirenOn = false;
                NativeFunction.Natives.SET_VEHICLE_LIGHTS(Vehicle, 1);
            }

            StartPatrolling();
        }

        private void StartPatrolling()
        {
            _currentState = PartnerState.Patrolling;
            _stateStartTime = DateTime.Now;
            _isDriving = false;
            _failedPathAttempts = 0;
            SetRandomPatrolDestination();
        }

        private bool IsValidDestination(Vector3 destination)
        {
            float distance = Ped.DistanceTo(destination);
            if (distance < 30f) return false;

            Vector3 dummy;
            return NativeFunction.Natives.GET_CLOSEST_VEHICLE_NODE<bool>(destination.X, destination.Y, destination.Z, out dummy, 1, 3f, 0);
        }

        private void SetRandomPatrolDestination()
        {
            if (!IsValid()) return;

            Vector3 playerPos = Game.LocalPlayer.Character.Position;

            for (int attempt = 0; attempt < 10; attempt++)
            {
                float angle = (float)(new Random().NextDouble() * Math.PI * 2);
                float distance = 200f + (float)(new Random().NextDouble() * 200f);

                float xOffset = (float)Math.Cos(angle) * distance;
                float yOffset = (float)Math.Sin(angle) * distance;

                Vector3 destination = new Vector3(
                    playerPos.X + xOffset,
                    playerPos.Y + yOffset,
                    playerPos.Z
                );

                Vector3 roadPos;
                if (NativeFunction.Natives.GET_CLOSEST_VEHICLE_NODE<bool>(destination.X, destination.Y, destination.Z, out roadPos, 1, 3f, 0))
                {
                    if (Ped.DistanceTo(roadPos) < 50f) continue;

                    _targetLocation = roadPos;

                    Ped.Tasks.Clear();

                    float speedMps = 12f;

                    NativeFunction.Natives.SET_DRIVE_TASK_DRIVING_STYLE(Ped, NormalDrivingStyle);
                    NativeFunction.Natives.TASK_VEHICLE_DRIVE_TO_COORD(
                        Ped, Vehicle,
                        _targetLocation.X, _targetLocation.Y, _targetLocation.Z,
                        speedMps, 1, Vehicle.Model.Hash, NormalDrivingStyle, 5f, 5f
                    );

                    _isDriving = true;
                    _failedPathAttempts = 0;
                    _stateStartTime = DateTime.Now;
                    return;
                }
            }

            _stateStartTime = DateTime.Now.AddSeconds(-20);
        }

        private void UpdatePatrol()
        {
            if (!IsValid()) return;

            if (_isDriving)
            {
                float distToTarget = Ped.DistanceTo(_targetLocation);
                float driveTime = (float)(DateTime.Now - _stateStartTime).TotalSeconds;

                if (distToTarget < 30f)
                {
                    _isDriving = false;
                    _stateStartTime = DateTime.Now;
                    Ped.Tasks.StandStill(5000);
                }
                else if (driveTime > 45f)
                {
                    _isDriving = false;
                    _stateStartTime = DateTime.Now;
                }
            }

            if (!_isDriving && (DateTime.Now - _stateStartTime).TotalSeconds > 6)
            {
                _stateStartTime = DateTime.Now;
                SetRandomPatrolDestination();
            }
        }

        private void UpdateResponse()
        {
            if (!IsValid()) return;

            try
            {
                // For traffic stop
                if (_currentState == PartnerState.RespondingToTrafficStop)
                {
                    float distToPlayer = Ped.DistanceTo(Game.LocalPlayer.Character.Position);
                    float distToTarget = Ped.DistanceTo(_targetLocation);

                    // Log distance for debugging
                    Game.LogTrivial($"1803 Partner - {CallSign} STATE: RespondingToTrafficStop");
                    Game.LogTrivial($"1803 Partner - {CallSign} distToPlayer: {distToPlayer:F1}m, distToTarget: {distToTarget:F1}m");
                    Game.LogTrivial($"1803 Partner - {CallSign} hasSetParkingTarget: {_hasSetParkingTarget}, hasExited: {_hasExitedVehicle}");

                    // FORCE EXIT WHEN VERY CLOSE TO PLAYER (regardless of parking target)
                    if (distToPlayer < 20f && !_hasExitedVehicle)
                    {
                        Game.LogTrivial($"1803 Partner - {CallSign} WITHIN 20m OF PLAYER - FORCING EXIT!");
                        Game.DisplayNotification($"~b~{CallSign}~w~: Moving into position.");

                        // Stop the vehicle
                        NativeFunction.Natives.SET_VEHICLE_FORWARD_SPEED(Vehicle, 0f);
                        Ped.Tasks.Clear();
                        GameFiber.Wait(500);

                        // Turn off siren
                        if (Vehicle && Vehicle.IsValid())
                        {
                            Vehicle.IsSirenOn = false;
                        }

                        // Exit vehicle immediately
                        StartTrafficStopApproach();
                        return;
                    }

                    // If we haven't set the parking target yet and we're close to player
                    if (!_hasSetParkingTarget && distToPlayer < 50f && _stoppedVehicle != null && _stoppedVehicle.Exists())
                    {
                        Game.LogTrivial($"1803 Partner - {CallSign} setting parking target");

                        // Calculate final parking position behind the stopped vehicle
                        Vector3 parkingPosition = _stoppedVehicle.GetOffsetPosition(new Vector3(-8f, 0f, 0f));

                        // Find nearest road
                        Vector3 roadPos;
                        if (NativeFunction.Natives.GET_CLOSEST_VEHICLE_NODE<bool>(parkingPosition.X, parkingPosition.Y, parkingPosition.Z, out roadPos, 1, 3f, 0))
                        {
                            parkingPosition = roadPos;
                        }

                        // Get ground Z
                        float groundZ;
                        if (NativeFunction.Natives.GET_GROUND_Z_FOR_3D_COORD(parkingPosition.X, parkingPosition.Y, parkingPosition.Z + 5f, out groundZ, false))
                        {
                            parkingPosition.Z = groundZ;
                        }

                        _targetLocation = parkingPosition;
                        _hasSetParkingTarget = true;

                        // Clear current task and drive to parking spot
                        Ped.Tasks.Clear();
                        float speedMps = 5f; // Very slow speed for parking
                        NativeFunction.Natives.SET_DRIVE_TASK_DRIVING_STYLE(Ped, NormalDrivingStyle);
                        NativeFunction.Natives.TASK_VEHICLE_DRIVE_TO_COORD(
                            Ped, Vehicle,
                            _targetLocation.X, _targetLocation.Y, _targetLocation.Z,
                            speedMps, 1, Vehicle.Model.Hash, NormalDrivingStyle, 2f, 2f
                        );

                        Game.DisplayNotification($"~b~{CallSign}~w~: Pulling into position.");
                    }
                }
                // For other response types
                else if (Ped.DistanceTo(_targetLocation) < 30f)
                {
                    if (_currentState == PartnerState.ReturningToPlayer)
                    {
                        Game.DisplayNotification($"~b~{CallSign}~w~: I'm back in the area.");
                        _currentState = PartnerState.Patrolling;
                        StartPatrolling();
                    }
                    else if (_currentState == PartnerState.RespondingToPursuit ||
                             _currentState == PartnerState.RespondingToCallout)
                    {
                        Game.DisplayNotification($"~b~{CallSign}~w~: I'm on scene.");

                        if (Vehicle && Vehicle.IsValid())
                        {
                            Vehicle.IsSirenOn = false;
                        }

                        StartPatrolling();
                    }
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"1803 Partner - UpdateResponse error: {ex.Message}");
            }
        }

        private void UpdateApproach()
        {
            if (!IsValid() || _stoppedVehicle == null || !_stoppedVehicle.Exists()) return;

            try
            {
                float distToTarget = Ped.DistanceTo(_targetLocation);
                Game.LogTrivial($"1803 Partner - {CallSign} approach distance: {distToTarget:F1}m");

                if (distToTarget < 2f)
                {
                    Game.LogTrivial($"1803 Partner - {CallSign} reached approach position");

                    // Face the stopped vehicle
                    NativeFunction.Natives.TASK_TURN_PED_TO_FACE_ENTITY(Ped, _stoppedVehicle, 2000);

                    _currentState = PartnerState.OnTrafficStopScene;
                    Game.DisplayNotification($"~b~{CallSign}~w~: I'll watch your back.");
                }
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"1803 Partner - UpdateApproach error: {ex.Message}");
                _currentState = PartnerState.OnTrafficStopScene;
            }
        }

        private void StartTrafficStopApproach()
        {
            try
            {
                if (!IsValid()) return;

                Game.LogTrivial($"1803 Partner - {CallSign} STARTING APPROACH");

                // Make sure we have a stopped vehicle
                if (_stoppedVehicle == null || !_stoppedVehicle.Exists())
                {
                    Ped player = Game.LocalPlayer.Character;
                    if (player != null && player.Exists())
                    {
                        _stoppedVehicle = player.CurrentVehicle;
                    }

                    if (_stoppedVehicle == null || !_stoppedVehicle.Exists())
                    {
                        Game.LogTrivial($"1803 Partner - {CallSign} no stopped vehicle, cannot approach");
                        _currentState = PartnerState.Patrolling;
                        return;
                    }
                }

                Game.DisplayNotification($"~b~{CallSign}~w~: Approaching on foot.");

                // Turn off siren but keep lights on
                if (Vehicle && Vehicle.IsValid())
                {
                    Vehicle.IsSirenOn = false;
                    NativeFunction.Natives.SET_VEHICLE_LIGHTS(Vehicle, 2);
                    Game.LogTrivial($"1803 Partner - {CallSign} sirens off, lights on");
                }

                // Exit vehicle
                Game.LogTrivial($"1803 Partner - {CallSign} exiting vehicle");
                Ped.Tasks.LeaveVehicle(LeaveVehicleFlags.None);

                // Wait for exit
                int timeout = 0;
                while (timeout < 50 && Ped.IsInVehicle(Vehicle, false))
                {
                    GameFiber.Wait(50);
                    timeout++;
                }

                // Force exit if needed
                if (Ped.IsInVehicle(Vehicle, false))
                {
                    Game.LogTrivial($"1803 Partner - {CallSign} forcing exit");
                    Vector3 exitPos = Vehicle.GetOffsetPosition(new Vector3(-2f, -1f, 0f));
                    Ped.Position = exitPos;
                }

                GameFiber.Wait(500);

                _hasExitedVehicle = true;

                // Calculate approach position near driver's side
                Vector3 approachPosition = _stoppedVehicle.GetOffsetPosition(new Vector3(-4f, -3f, 0f));

                // Ensure position is on ground
                float groundZ;
                if (NativeFunction.Natives.GET_GROUND_Z_FOR_3D_COORD(approachPosition.X, approachPosition.Y, approachPosition.Z + 5f, out groundZ, false))
                {
                    approachPosition.Z = groundZ;
                }

                // Store as target
                _targetLocation = approachPosition;
                _currentState = PartnerState.ApproachingTrafficStop;

                // Walk to position
                Game.LogTrivial($"1803 Partner - {CallSign} walking to approach position");
                NativeFunction.Natives.TASK_GO_STRAIGHT_TO_COORD(Ped, approachPosition.X, approachPosition.Y, approachPosition.Z, 1.5f, 5000, 0f, 0.5f);
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"1803 Partner - StartTrafficStopApproach error: {ex.Message}");
                _currentState = PartnerState.OnTrafficStopScene;
            }
        }

        private void DriveToLocation(Vector3 location, float speedMph, bool emergency)
        {
            if (!IsValid()) return;

            try
            {
                Vector3 roadPos;
                if (!NativeFunction.Natives.GET_CLOSEST_VEHICLE_NODE<bool>(location.X, location.Y, location.Z, out roadPos, 1, 3f, 0))
                {
                    roadPos = Game.LocalPlayer.Character.Position;
                }

                _targetLocation = roadPos;
                float speedMps = speedMph * 0.44704f;

                Ped.Tasks.Clear();

                uint drivingStyle = emergency ? EmergencyDrivingStyle : NormalDrivingStyle;

                if (emergency && Vehicle.Exists())
                {
                    NativeFunction.Natives.SET_VEHICLE_SIREN(Vehicle, true);
                    NativeFunction.Natives.SET_VEHICLE_LIGHTS(Vehicle, 2);
                }

                NativeFunction.Natives.SET_DRIVE_TASK_DRIVING_STYLE(Ped, drivingStyle);
                NativeFunction.Natives.TASK_VEHICLE_DRIVE_TO_COORD(
                    Ped, Vehicle,
                    _targetLocation.X, _targetLocation.Y, _targetLocation.Z,
                    speedMps, 1, Vehicle.Model.Hash, drivingStyle, 10f, 10f
                );

                _isDriving = true;
                _stateStartTime = DateTime.Now;

                Game.LogTrivial($"1803 Partner - {CallSign} driving to {location}");
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"1803 Partner - DriveToLocation error: {ex.Message}");
            }
        }

        // Static method to get the nearest available partner
        public static Partner GetNearestPartner(Vector3 position, PartnerState requiredState = PartnerState.Patrolling)
        {
            lock (_allPartners)
            {
                // Remove any invalid partners
                _allPartners.RemoveAll(p => !p.IsValid());

                // Find partners that are patrolling (available to respond)
                var availablePartners = _allPartners.Where(p => p._currentState == PartnerState.Patrolling).ToList();

                if (availablePartners.Count == 0)
                    return null;

                // Return the closest one
                return availablePartners.OrderBy(p => p.Ped.DistanceTo(position)).FirstOrDefault();
            }
        }

        // Static method to respond to traffic stop with nearest partner
        public static bool RespondNearestToTrafficStop()
        {
            Vector3 playerPos = Game.LocalPlayer.Character.Position;
            Partner nearest = GetNearestPartner(playerPos);

            if (nearest != null)
            {
                Game.LogTrivial($"1803 Partner - Nearest partner {nearest.CallSign} responding to traffic stop");
                nearest.RespondToTrafficStop();
                return true;
            }
            else
            {
                Game.DisplayNotification("~r~No available partners nearby.");
                return false;
            }
        }

        // Static method to respond to pursuit with nearest partner
        public static bool RespondNearestToPursuit()
        {
            Vector3 playerPos = Game.LocalPlayer.Character.Position;
            Partner nearest = GetNearestPartner(playerPos);

            if (nearest != null)
            {
                Game.LogTrivial($"1803 Partner - Nearest partner {nearest.CallSign} responding to pursuit");
                nearest.RespondToPursuit();
                return true;
            }
            else
            {
                Game.DisplayNotification("~r~No available partners nearby.");
                return false;
            }
        }

        // Static method to respond to callout with nearest partner
        public static bool RespondNearestToCallout()
        {
            Vector3 playerPos = Game.LocalPlayer.Character.Position;
            Partner nearest = GetNearestPartner(playerPos);

            if (nearest != null)
            {
                Game.LogTrivial($"1803 Partner - Nearest partner {nearest.CallSign} responding to callout");
                nearest.RespondToCallout();
                return true;
            }
            else
            {
                Game.DisplayNotification("~r~No available partners nearby.");
                return false;
            }
        }

        public void RespondToTrafficStop()
        {
            if (!IsValid()) return;

            try
            {
                _currentState = PartnerState.RespondingToTrafficStop;
                _hasSetParkingTarget = false;
                _hasExitedVehicle = false;

                if (Vehicle && Vehicle.IsValid())
                {
                    Vehicle.IsSirenOn = true;
                    Vehicle.IsSirenSilent = false;
                    NativeFunction.Natives.SET_VEHICLE_LIGHTS(Vehicle, 2);
                }

                // Find the stopped vehicle
                Ped player = Game.LocalPlayer.Character;
                if (player == null || !player.Exists()) return;

                Vehicle playerVehicle = player.CurrentVehicle;

                if (playerVehicle == null || !playerVehicle.Exists())
                {
                    Game.DisplayNotification("~r~Cannot find player vehicle.");
                    return;
                }

                // Store the stopped vehicle
                _stoppedVehicle = playerVehicle;

                // DRIVE TO PLAYER'S LOCATION
                Game.DisplayNotification($"~b~{CallSign}~w~: Responding Code 3 to your traffic stop.");
                Game.LogTrivial($"1803 Partner - {CallSign} driving to player at {player.Position}");

                DriveToLocation(player.Position, 40f, true);
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"1803 Partner - RespondToTrafficStop error: {ex.Message}");
            }
        }

        public void RespondToPursuit()
        {
            if (!IsValid()) return;

            try
            {
                _currentState = PartnerState.RespondingToPursuit;

                if (Vehicle && Vehicle.IsValid())
                {
                    Vehicle.IsSirenOn = true;
                    Vehicle.IsSirenSilent = false;
                    NativeFunction.Natives.SET_VEHICLE_LIGHTS(Vehicle, 2);
                }

                DriveToLocation(Game.LocalPlayer.Character.Position, 70f, true);
                Game.DisplayNotification($"~b~{CallSign}~w~: Responding to your location.");
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"1803 Partner - RespondToPursuit error: {ex.Message}");
            }
        }

        public void RespondToCallout()
        {
            if (!IsValid()) return;

            try
            {
                _currentState = PartnerState.RespondingToCallout;

                if (Vehicle && Vehicle.IsValid())
                {
                    Vehicle.IsSirenOn = true;
                    Vehicle.IsSirenSilent = false;
                    NativeFunction.Natives.SET_VEHICLE_LIGHTS(Vehicle, 2);
                }

                DriveToLocation(Game.LocalPlayer.Character.Position, 40f, true);
                Game.DisplayNotification($"~b~{CallSign}~w~: Responding to your callout.");
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"1803 Partner - RespondToCallout error: {ex.Message}");
            }
        }

        public void Dismiss()
        {
            try
            {
                lock (_allPartners)
                {
                    _allPartners.Remove(this);
                }

                if (Ped && Ped.IsValid()) Ped.Delete();
                if (Vehicle && Vehicle.IsValid()) Vehicle.Delete();
            }
            catch (Exception ex)
            {
                Game.LogTrivial($"1803 Partner - Dismiss error: {ex.Message}");
            }
        }

        // Static method to get all partners
        public static List<Partner> GetAllPartners()
        {
            lock (_allPartners)
            {
                _allPartners.RemoveAll(p => !p.IsValid());
                return new List<Partner>(_allPartners);
            }
        }
    }
}