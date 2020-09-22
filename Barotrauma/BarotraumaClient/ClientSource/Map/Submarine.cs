﻿using Barotrauma.Networking;
using Barotrauma.RuinGeneration;
using Barotrauma.Sounds;
using FarseerPhysics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections;
using System.Collections.Generic;
using Barotrauma.IO;
using System.Linq;
using System.Xml.Linq;
using Barotrauma.Items.Components;
using System.Globalization;

namespace Barotrauma
{
    class RoundSound
    {
        public Sound Sound;
        public readonly float Volume;
        public readonly float Range;
        public readonly Vector2 FrequencyMultiplierRange;
        public readonly bool Stream;

        public string Filename
        {
            get { return Sound?.Filename; }
        }

        public RoundSound(XElement element, Sound sound)
        {
            Sound = sound;
            Stream = sound.Stream;
            Range = element.GetAttributeFloat("range", 1000.0f);
            Volume = element.GetAttributeFloat("volume", 1.0f);
            FrequencyMultiplierRange = new Vector2(1.0f);
            string freqMultAttr = element.GetAttributeString("frequencymultiplier", element.GetAttributeString("frequency", "1.0"));
            if (!freqMultAttr.Contains(','))
            {
                if (float.TryParse(freqMultAttr, NumberStyles.Any, CultureInfo.InvariantCulture, out float freqMult))
                {
                    FrequencyMultiplierRange = new Vector2(freqMult);
                }
            }
            else
            {
                var freqMult = XMLExtensions.ParseVector2(freqMultAttr, false);
                if (freqMult.Y >= 0.25f)
                {
                    FrequencyMultiplierRange = freqMult;
                }
            }
            if (FrequencyMultiplierRange.Y > 4.0f)
            {
                DebugConsole.ThrowError($"Loaded frequency range exceeds max value: {FrequencyMultiplierRange} (original string was \"{freqMultAttr}\")");
            }
            sound.IgnoreMuffling = element.GetAttributeBool("dontmuffle", false);
        }

        public float GetRandomFrequencyMultiplier()
        {
            return Rand.Range(FrequencyMultiplierRange.X, FrequencyMultiplierRange.Y);
        }
    }

    partial class Submarine : Entity, IServerSerializable
    {
        public static Vector2 MouseToWorldGrid(Camera cam, Submarine sub)
        {
            Vector2 position = PlayerInput.MousePosition;
            position = cam.ScreenToWorld(position);

            Vector2 worldGridPos = VectorToWorldGrid(position);

            if (sub != null)
            {
                worldGridPos.X += sub.Position.X % GridSize.X;
                worldGridPos.Y += sub.Position.Y % GridSize.Y;
            }

            return worldGridPos;
        }


        private static List<RoundSound> roundSounds = null;
        public static RoundSound LoadRoundSound(XElement element, bool stream = false)
        {
            if (GameMain.SoundManager?.Disabled ?? true) { return null; }

            string filename = element.GetAttributeString("file", "");
            if (string.IsNullOrEmpty(filename)) filename = element.GetAttributeString("sound", "");

            if (string.IsNullOrEmpty(filename))
            {
                string errorMsg = "Error when loading round sound (" + element + ") - file path not set";
                DebugConsole.ThrowError(errorMsg);
                GameAnalyticsManager.AddErrorEventOnce("Submarine.LoadRoundSound:FilePathEmpty" + element.ToString(), GameAnalyticsSDK.Net.EGAErrorSeverity.Error, errorMsg + "\n" + Environment.StackTrace);
                return null;
            }

            filename = Path.GetFullPath(filename.CleanUpPath()).CleanUpPath();
            Sound existingSound = null;
            if (roundSounds == null)
            {
                roundSounds = new List<RoundSound>();
            }
            else
            {
                existingSound = roundSounds.Find(s => s.Filename == filename && s.Stream == stream)?.Sound;
            }

            if (existingSound == null)
            {
                try
                {
                    existingSound = GameMain.SoundManager.LoadSound(filename, stream);
                    if (existingSound == null) { return null; }
                }
                catch (System.IO.FileNotFoundException e)
                {
                    string errorMsg = "Failed to load sound file \"" + filename + "\".";
                    DebugConsole.ThrowError(errorMsg, e);
                    GameAnalyticsManager.AddErrorEventOnce("Submarine.LoadRoundSound:FileNotFound" + filename, GameAnalyticsSDK.Net.EGAErrorSeverity.Error, errorMsg + "\n" + Environment.StackTrace);
                    return null;
                }
            }

            RoundSound newSound = new RoundSound(element, existingSound);

            roundSounds.Add(newSound);
            return newSound;
        }

        private static void RemoveRoundSound(RoundSound roundSound)
        {
            roundSound.Sound?.Dispose();
            if (roundSounds == null) return;

            if (roundSounds.Contains(roundSound)) roundSounds.Remove(roundSound);
            foreach (RoundSound otherSound in roundSounds)
            {
                if (otherSound.Sound == roundSound.Sound) otherSound.Sound = null;
            }
        }

        public static void RemoveAllRoundSounds()
        {
            if (roundSounds == null) return;
            for (int i = roundSounds.Count - 1; i >= 0; i--)
            {
                RemoveRoundSound(roundSounds[i]);
            }
        }

        //drawing ----------------------------------------------------
        private static readonly HashSet<Submarine> visibleSubs = new HashSet<Submarine>();
        private static readonly HashSet<Ruin> visibleRuins = new HashSet<Ruin>();
        public static void CullEntities(Camera cam)
        {
            visibleSubs.Clear();
            foreach (Submarine sub in Loaded)
            {
                if (sub.WorldPosition.Y < Level.MaxEntityDepth) continue;

                Rectangle worldBorders = new Rectangle(
                    sub.Borders.X + (int)sub.WorldPosition.X - 500,
                    sub.Borders.Y + (int)sub.WorldPosition.Y + 500,
                    sub.Borders.Width + 1000,
                    sub.Borders.Height + 1000);

                if (RectsOverlap(worldBorders, cam.WorldView))
                {
                    visibleSubs.Add(sub);
                }
            }

            visibleRuins.Clear();
            if (Level.Loaded != null)
            {
                foreach (Ruin ruin in Level.Loaded.Ruins)
                {
                    Rectangle worldBorders = new Rectangle(
                        ruin.Area.X - 500,
                        ruin.Area.Y + ruin.Area.Height + 500,
                        ruin.Area.Width + 1000,
                        ruin.Area.Height + 1000);

                    if (RectsOverlap(worldBorders, cam.WorldView))
                    {
                        visibleRuins.Add(ruin);
                    }
                }
            }

            if (visibleEntities == null)
            {
                visibleEntities = new List<MapEntity>(MapEntity.mapEntityList.Count);
            }
            else
            {
                visibleEntities.Clear();
            }

            Rectangle worldView = cam.WorldView;
            foreach (MapEntity entity in MapEntity.mapEntityList)
            {
                if (entity.Submarine != null)
                {
                    if (!visibleSubs.Contains(entity.Submarine)) { continue; }
                }
                else if (entity.ParentRuin != null)
                {
                    if (!visibleRuins.Contains(entity.ParentRuin)) { continue; }
                }

                if (entity.IsVisible(worldView)) { visibleEntities.Add(entity); }
            }
        }

        public static void Draw(SpriteBatch spriteBatch, bool editing = false)
        {
            var entitiesToRender = !editing && visibleEntities != null ? visibleEntities : MapEntity.mapEntityList;

            foreach (MapEntity e in entitiesToRender)
            {
                e.Draw(spriteBatch, editing);
            }
        }

        public static void DrawFront(SpriteBatch spriteBatch, bool editing = false, Predicate<MapEntity> predicate = null)
        {
            var entitiesToRender = !editing && visibleEntities != null ? visibleEntities : MapEntity.mapEntityList;

            foreach (MapEntity e in entitiesToRender)
            {
                if (!e.DrawOverWater) continue;

                if (predicate != null)
                {
                    if (!predicate(e)) continue;
                }

                e.Draw(spriteBatch, editing, false);
            }

            if (GameMain.DebugDraw)
            {
                foreach (Submarine sub in Loaded)
                {
                    Rectangle worldBorders = sub.Borders;
                    worldBorders.Location += sub.WorldPosition.ToPoint();
                    worldBorders.Y = -worldBorders.Y;

                    GUI.DrawRectangle(spriteBatch, worldBorders, Color.White, false, 0, 5);

                    if (sub.SubBody == null || sub.subBody.PositionBuffer.Count < 2) continue;

                    Vector2 prevPos = ConvertUnits.ToDisplayUnits(sub.subBody.PositionBuffer[0].Position);
                    prevPos.Y = -prevPos.Y;

                    for (int i = 1; i < sub.subBody.PositionBuffer.Count; i++)
                    {
                        Vector2 currPos = ConvertUnits.ToDisplayUnits(sub.subBody.PositionBuffer[i].Position);
                        currPos.Y = -currPos.Y;

                        GUI.DrawRectangle(spriteBatch, new Rectangle((int)currPos.X - 10, (int)currPos.Y - 10, 20, 20), Color.Blue * 0.6f, true, 0.01f);
                        GUI.DrawLine(spriteBatch, prevPos, currPos, Color.Cyan * 0.5f, 0, 5);

                        prevPos = currPos;
                    }
                }
            }
        }

        public static float DamageEffectCutoff;
        public static Color DamageEffectColor;

        private static readonly List<Structure> depthSortedDamageable = new List<Structure>();
        public static void DrawDamageable(SpriteBatch spriteBatch, Effect damageEffect, bool editing = false, Predicate<MapEntity> predicate = null)
        {
            var entitiesToRender = !editing && visibleEntities != null ? visibleEntities : MapEntity.mapEntityList;

            depthSortedDamageable.Clear();

            //insertion sort according to draw depth
            foreach (MapEntity e in entitiesToRender)
            {
                if (e is Structure structure && structure.DrawDamageEffect)
                {
                    if (predicate != null)
                    {
                        if (!predicate(e)) continue;
                    }
                    float drawDepth = structure.GetDrawDepth();
                    int i = 0;
                    while (i < depthSortedDamageable.Count)
                    {
                        float otherDrawDepth = depthSortedDamageable[i].GetDrawDepth();
                        if (otherDrawDepth < drawDepth) { break; }
                        i++;
                    }
                    depthSortedDamageable.Insert(i, structure);
                }
            }
            
            foreach (Structure s in depthSortedDamageable)
            {
                s.DrawDamage(spriteBatch, damageEffect, editing);
            }
            if (damageEffect != null)
            {
                damageEffect.Parameters["aCutoff"].SetValue(0.0f);
                damageEffect.Parameters["cCutoff"].SetValue(0.0f);
                DamageEffectCutoff = 0.0f;
            }
        }

        public static void DrawPaintedColors(SpriteBatch spriteBatch, bool editing = false, Predicate<MapEntity> predicate = null)
        {
            var entitiesToRender = !editing && visibleEntities != null ? visibleEntities : MapEntity.mapEntityList;

            foreach (MapEntity e in entitiesToRender)
            {
                if (e is Hull hull)
                {
                    if (hull.SupportsPaintedColors)
                    {
                        if (predicate != null)
                        {
                            if (!predicate(e)) continue;
                        }

                        hull.DrawSectionColors(spriteBatch);
                    }
                }
            }
        }

        public static void DrawBack(SpriteBatch spriteBatch, bool editing = false, Predicate<MapEntity> predicate = null)
        {
            var entitiesToRender = !editing && visibleEntities != null ? visibleEntities : MapEntity.mapEntityList;

            foreach (MapEntity e in entitiesToRender)
            {
                if (!e.DrawBelowWater) continue;

                if (predicate != null)
                {
                    if (!predicate(e)) continue;
                }

                e.Draw(spriteBatch, editing, true);
            }
        }

        public static void DrawGrid(SpriteBatch spriteBatch, int gridCells, Vector2 gridCenter, Vector2 roundedGridCenter, float alpha = 1.0f)
        {
            var horizontalLine = GUI.Style.GetComponentStyle("HorizontalLine").GetDefaultSprite();
            var verticalLine = GUI.Style.GetComponentStyle("VerticalLine").GetDefaultSprite();

            Vector2 topLeft = roundedGridCenter - Vector2.One * GridSize * gridCells / 2;
            Vector2 bottomRight = roundedGridCenter + Vector2.One * GridSize * gridCells / 2;

            for (int i = 0; i < gridCells; i++)
            {
                float distFromGridX = (MathUtils.RoundTowardsClosest(gridCenter.X, GridSize.X) - gridCenter.X) / GridSize.X;
                float distFromGridY = (MathUtils.RoundTowardsClosest(gridCenter.X, GridSize.Y) - gridCenter.X) / GridSize.Y;

                float normalizedDistX = Math.Abs(i + distFromGridX - gridCells / 2) / (gridCells / 2);
                float normalizedDistY = Math.Abs(i - distFromGridY - gridCells / 2) / (gridCells / 2);

                float expandX = MathHelper.Lerp(30.0f, 0.0f, normalizedDistX);
                float expandY = MathHelper.Lerp(30.0f, 0.0f, normalizedDistY);

                GUI.DrawLine(spriteBatch,
                    horizontalLine,
                    new Vector2(topLeft.X - expandX, -bottomRight.Y + i * GridSize.Y),
                    new Vector2(bottomRight.X + expandX, -bottomRight.Y + i * GridSize.Y),
                    Color.White * (1.0f - normalizedDistY) * alpha, depth: 0.6f, width: 3);
                GUI.DrawLine(spriteBatch,
                    verticalLine,
                    new Vector2(topLeft.X + i * GridSize.X, -topLeft.Y + expandY),
                    new Vector2(topLeft.X + i * GridSize.X, -bottomRight.Y - expandY),
                    Color.White * (1.0f - normalizedDistX) * alpha, depth: 0.6f, width: 3);
            }
        }

        public void CreateMiniMap(GUIComponent parent, IEnumerable<Entity> pointsOfInterest = null, bool ignoreOutpost = false)
        {
            Rectangle worldBorders = GetDockedBorders();
            worldBorders.Location += WorldPosition.ToPoint();

            //create a container that has the same "aspect ratio" as the sub
            float aspectRatio = worldBorders.Width / (float)worldBorders.Height;
            float parentAspectRatio = parent.Rect.Width / (float)parent.Rect.Height;

            float scale = 0.9f;

            GUIFrame hullContainer = new GUIFrame(new RectTransform(
                (parentAspectRatio > aspectRatio ? new Vector2(aspectRatio / parentAspectRatio, 1.0f) : new Vector2(1.0f, parentAspectRatio / aspectRatio)) * scale, 
                parent.RectTransform, Anchor.Center), 
                style: null);

            foreach (Hull hull in Hull.hullList)
            {
                if (hull.Submarine != this && !(DockedTo.Contains(hull.Submarine))) continue;
                if (ignoreOutpost && !IsEntityFoundOnThisSub(hull, true)) { continue; }

                Vector2 relativeHullPos = new Vector2(
                    (hull.WorldRect.X - worldBorders.X) / (float)worldBorders.Width, 
                    (worldBorders.Y - hull.WorldRect.Y) / (float)worldBorders.Height);
                Vector2 relativeHullSize = new Vector2(hull.Rect.Width / (float)worldBorders.Width, hull.Rect.Height / (float)worldBorders.Height);

                var hullFrame = new GUIFrame(new RectTransform(relativeHullSize, hullContainer.RectTransform) { RelativeOffset = relativeHullPos }, style: "MiniMapRoom", color: Color.DarkCyan * 0.8f)
                {
                    UserData = hull
                };
                new GUIFrame(new RectTransform(Vector2.One, hullFrame.RectTransform), style: "ScanLines", color: Color.DarkCyan * 0.8f);
            }

            if (pointsOfInterest != null)
            {
                foreach (Entity entity in pointsOfInterest)
                {
                    Vector2 relativePos = new Vector2(
                        (entity.WorldPosition.X - worldBorders.X) / worldBorders.Width,
                        (worldBorders.Y - entity.WorldPosition.Y) / worldBorders.Height);
                    new GUIFrame(new RectTransform(new Point(1, 1), hullContainer.RectTransform) { RelativeOffset = relativePos }, style: null)
                    {
                        CanBeFocused = false,
                        UserData = entity
                    };
                }
            }
        }

        public void CheckForErrors()
        {
            List<string> errorMsgs = new List<string>();

            if (!Hull.hullList.Any())
            {
                errorMsgs.Add(TextManager.Get("NoHullsWarning"));
            }

            if (Info.Type != SubmarineType.OutpostModule || 
                (Info.OutpostModuleInfo?.ModuleFlags.Any(f => !f.Equals("hallwayvertical", StringComparison.OrdinalIgnoreCase) && !f.Equals("hallwayhorizontal", StringComparison.OrdinalIgnoreCase)) ?? true))
            {
                if (!WayPoint.WayPointList.Any(wp => wp.ShouldBeSaved && wp.SpawnType == SpawnType.Path))
                {
                    errorMsgs.Add(TextManager.Get("NoWaypointsWarning"));
                }
            }

            if (Info.Type == SubmarineType.Player)
            {
                foreach (Item item in Item.ItemList)
                {
                    if (item.GetComponent<Items.Components.Vent>() == null) { continue; }
                    if (!item.linkedTo.Any())
                    {
                        errorMsgs.Add(TextManager.Get("DisconnectedVentsWarning"));
                        break;
                    }
                }

                if (!WayPoint.WayPointList.Any(wp => wp.ShouldBeSaved && wp.SpawnType == SpawnType.Human))
                {
                    errorMsgs.Add(TextManager.Get("NoHumanSpawnpointWarning"));
                }
                if (WayPoint.WayPointList.Find(wp => wp.SpawnType == SpawnType.Cargo) == null)
                {
                    errorMsgs.Add(TextManager.Get("NoCargoSpawnpointWarning"));
                }
                if (!Item.ItemList.Any(it => it.GetComponent<Items.Components.Pump>() != null && it.HasTag("ballast")))
                {
                    errorMsgs.Add(TextManager.Get("NoBallastTagsWarning"));
                }
            }
            else if (Info.Type == SubmarineType.OutpostModule)
            {
                foreach (Item item in Item.ItemList)
                {
                    var junctionBox = item.GetComponent<PowerTransfer>();
                    if (junctionBox == null) { continue; }
                    int doorLinks =
                        item.linkedTo.Count(lt => lt is Gap || (lt is Item it2 && it2.GetComponent<Door>() != null)) +
                        Item.ItemList.Count(it2 => it2.linkedTo.Contains(item) && !item.linkedTo.Contains(it2));
                    for (int i = 0; i < item.Connections.Count; i++)
                    {
                        int wireCount = item.Connections[i].Wires.Count(w => w != null);
                        if (doorLinks + wireCount > Connection.MaxLinked)
                        {
                            errorMsgs.Add(TextManager.GetWithVariables("InsufficientFreeConnectionsWarning", 
                                new string[] { "[doorcount]", "[freeconnectioncount]" },
                                new string[] { doorLinks.ToString(), (Connection.MaxLinked - wireCount).ToString() }));
                            break;
                        }
                    }
                }
            }

            if (Gap.GapList.Any(g => g.linkedTo.Count == 0))
            {
                errorMsgs.Add(TextManager.Get("NonLinkedGapsWarning"));
            }

            int disabledItemLightCount = 0;
            foreach (Item item in Item.ItemList)
            {
                if (item.ParentInventory == null) { continue; }
                disabledItemLightCount += item.GetComponents<Items.Components.LightComponent>().Count();
            }
            int count = GameMain.LightManager.Lights.Count(l => l.CastShadows) - disabledItemLightCount;
            if (count > 45)
            {
                errorMsgs.Add(TextManager.Get("subeditor.shadowcastinglightswarning"));
            }

            if (errorMsgs.Any())
            {
                new GUIMessageBox(TextManager.Get("Warning"), string.Join("\n\n", errorMsgs), new Vector2(0.25f, 0.0f), new Point(400, 200));
            }

            foreach (MapEntity e in MapEntity.mapEntityList)
            {
                if (Vector2.Distance(e.Position, HiddenSubPosition) > 20000)
                {
                    //move disabled items (wires, items inside containers) inside the sub
                    if (e is Item item && item.body != null && !item.body.Enabled)
                    {
                        item.SetTransform(ConvertUnits.ToSimUnits(HiddenSubPosition), 0.0f);
                    }
                }
            }

            foreach (MapEntity e in MapEntity.mapEntityList)
            {
                if (Vector2.Distance(e.Position, HiddenSubPosition) > 20000)
                {
                    var msgBox = new GUIMessageBox(
                        TextManager.Get("Warning"),
                        TextManager.Get("FarAwayEntitiesWarning"),
                        new string[] { TextManager.Get("Yes"), TextManager.Get("No") });

                    msgBox.Buttons[0].OnClicked += (btn, obj) =>
                    {
                        GameMain.SubEditorScreen.Cam.Position = e.WorldPosition;
                        return true;
                    };
                    msgBox.Buttons[0].OnClicked += msgBox.Close;
                    msgBox.Buttons[1].OnClicked += msgBox.Close;

                    break;

                }
            }
        }
        
        public void ClientRead(ServerNetObject type, IReadMessage msg, float sendingTime)
        {
            if (type != ServerNetObject.ENTITY_POSITION)
            {
                DebugConsole.NewMessage($"Error while reading a network event for the submarine \"{Info.Name} ({ID})\". Invalid event type ({type}).", Color.Red);
            }

            var posInfo = PhysicsBody.ClientRead(type, msg, sendingTime, parentDebugName: Info.Name);
            msg.ReadPadBits();

            if (posInfo != null)
            {
                int index = 0;
                while (index < subBody.PositionBuffer.Count && sendingTime > subBody.PositionBuffer[index].Timestamp)
                {
                    index++;
                }

                subBody.PositionBuffer.Insert(index, posInfo);
            }
        }
    }
}
