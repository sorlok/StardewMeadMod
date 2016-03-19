/****
    Copyright 2016 S'orlok Reaves (Seth N. Hetu)

    See bottom of this file for license information.
    In short, it's BSD-style and you can use it freely.
*****/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Storm.ExternalEvent;
using Storm.StardewValley.Event;
using Storm.StardewValley.Wrapper;
using Storm.StardewValley.Proxy;
using Storm.StardewValley.Accessor;
using Microsoft.Xna.Framework;


//Putting your mod in its own namespace is a great idea!
namespace MeadMod
{
    [Mod]
    public class MeadMod : DiskResource
    {
        //This variable contains our mod configuration, loaded from the json config file.
        public Config ModConfig { get; private set; }


        //The Storm API uses "callbacks" to handle everything in a same manner.
        //Each callback is tagged with "Subscribe", and is identified by 
        // its name argument type (XYZ_Event). The function name doesn't matter,
        // so I usually call all my functions "UpdateCallback", based on the mod examples
        // I used when learning Storm.
        //The UpdateCallback(InitializeEvent) is used to initialize your module,
        // and is called exactly once when the program starts.
        //This is a good place to load config settings and perform basic checks.
        [Subscribe]
        public void UpdateCallback(InitializeEvent @event)
        {
            //In the event that this is called twice, do *not* reload our config.
            if (ModConfig != null)
            {
                return;
            }

            //Our config file will live inside our mod's folder.
            var configLocation = Path.Combine(PathOnDisk, "Config.json");

            //If the file doesn't exist, create it.
            if (!File.Exists(configLocation))
            {
                Console.WriteLine("The config file for MeadMod was not found, attempting creation...");
                Config c = new Config();
                c.EnablePlugin = true;
                File.WriteAllBytes(configLocation, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(c)));
            }

            //Once we are sure the file exists, load it.
            ModConfig = JsonConvert.DeserializeObject<Config>(Encoding.UTF8.GetString(File.ReadAllBytes(configLocation)));
            Console.WriteLine("The config file for MeadMod has been loaded.");
            Console.WriteLine("\tEnablePlugin: " + ModConfig.EnablePlugin);

            //Possibly do other stuff, then alert that we are done.
            Console.WriteLine("MeadMod Initialization Completed");
        }


        //The UpdateCallback(AssetLoadEvent) is a very useful hook: it is called each time the 
        // game tries to load a resource. This includes image files (tilesets, characters, etc.) 
        // and dictionaries (e.g., ObjectInfo.xnb). You can hook this to look for resources
        // in your own mod folder, and you can get pretty clever about how you redirect resources.
        //All callbacks allow you to override what is returned to the caller, and let you exit early
        // out of the (StardewValley) caller's function. Use the @event.ReturnValue and 
        // @event.ReturnEarly fields to do this.
        [Subscribe]
        public void UpdateCallback(AssetLoadEvent @event)
        {
            //This callback actually occurs before InitializeEvent, so we need to load
            // our config here. (Ideally, we would have a super-early fundtion hook that does this.)
            //This is not good design, but it's easy to read.
            if (ModConfig == null)
            {
                //Just dispatch to the actual callback function.
                UpdateCallback(new InitializeEvent());
            }

            //All of our hooks check if the plugin is disabled in config and return early (without touching anything) if that's the case.
            if (!ModConfig.EnablePlugin)
            {
                return;
            }

            //The first thing we hook is the "Maps\springobjects" graphic, a misleadingly-named tileset that contains all inventory-holdable
            // objects. Note that we want "Maps\springobjects", NOT just "springobjects".
            //We use Storm's "LoadResource" function to load a replacement png from our module's folder; this replacement graphic has
            // the Mead and Vodka tiles added in a new row at the bottom of the image. 
            //Note that this makes the mod incompatible with most other hacks; ideally, we want some way to add *just* our new row of graphics
            // here, but that currently is not easily doable.
            if (@event.Name == "Maps\\springobjects")
            {
                var path = Path.Combine(PathOnDisk, "springobjects.png");
                @event.ReturnValue = @event.Root.LoadResource(path);
                @event.ReturnEarly = true;
                Console.WriteLine("Overriding resource: " + @event.Name + " with: " + @event.ReturnValue);
            }

            //The second thing we need to over-ride is "Data/ObjectInformation". This YAML dictionary contains a magic string for each 
            // ObjectId that describes how much health it restores, how much it sells for, its name, its description, what category
            // that item is bucketed into (in our case, both Mead and Vodka are "drink"), and some other flags related to status that
            // I don't understand yet.
            //We want to return our own dictionary, so I wrote a small function that takes a text file of the form:
            //   ObjectId <Tab> MagicString
            //...and parses that into a dictionary of the appropriate type. It would be fairly easy to parse the original 
            // Data/ObjectInformation first, and then insert our lines from ObjectInformation.txt into the dictionary after that,
            // but I'll leave that as an exercise to the reader.
            if (@event.Name == "Data\\ObjectInformation")
            {
                var res = ReadTabbedObjectDictionary(Path.Combine(PathOnDisk, "ObjectInformation.txt"), @event.Name);
                if (res != null)
                {
                    @event.ReturnValue = res;
                    @event.ReturnEarly = true;
                    Console.WriteLine("Overriding resource: " + @event.Name + " with: " + @event.ReturnValue);
                }
            }
        }


        //The UpdateCallback(PreObjectDropInActionEvent) is called each time the player is about to drop an object into 
        // another object. We need to check the names of both objects, and make sure we are dropping "Potato" or "Honey" into "Keg".
        //You can imagine making other objects from other drop actions. Note that it's very helpful to look at the disassembled code 
        // of these functions in StardewValley.exe (use something like ILSpy), because that tells us if we're missing anything by,
        // e.g., returning early versus setting some other property and letting the function continue like normal.
        [Subscribe]
        public void UpdateCallback(PreObjectDropInActionEvent @event)
        {
            //As always, do nothing if we're disabled.
            if (!ModConfig.EnablePlugin)
            {
                return;
            }

            //If "this" (the object being dropped on) already has an object it is cooking, don't do anything.
            if (@event.This.HeldObject != null)
            {
                return;
            }

            //If "this" is a "Keg"
            if (@event.This.Name.Equals("Keg"))
            {
                //..and we are dropping a "Potato".
                if (@event.ArgDroppedObject.Name.Equals("Potato"))
                {
                    //Tell the keg that we are now holding a "Vodka".
                    @event.This.HeldObject = MakeKegVodkaItem(@event);

                    //The "Probe" parameter tells us if the caller is just asking what's inside (not setting it). 
                    //When this is false, we need to start our cooking timer.
                    if (!@event.Probe)
                    {
                        //We reset the name here (just because that's what StardewValley does; I'm not convinced it's needed).
                        @event.This.HeldObject.Name = "Vodka";

                        //Audio not implemented yet.
                        //Game1.playSound("Ship");
                        //Game1.playSound("bubbles");

                        //Set how long until this object is fully cooked. The smallest you can (reasonably) set this to is "10", since it updates
                        // on 10-minute intervals (like most things in the game).
                        @event.This.MinutesUntilReady = 20000;

                        //"Drop-In" animation not implemented yet.
                        /*
                        @event.ArgWho.CurrentLocation.TemporarySprites.Add(new TemporaryAnimatedSprite(@event.Root.Animations, new Rectangle(256, 1856, 64, 128), 80f, 6, 999999, this.tileLocation * (float)Game1.tileSize + new Vector2(0f, (float)(-(float)Game1.tileSize * 2)), false, false, (this.tileLocation.Y + 1f) * (float)Game1.tileSize / 10000f + 0.0001f, 0f, Color.Yellow * 0.75f, 1f, 0f, 0f, 0f, false)
                        {
                            alphaFade = 0.005f
                        });
                        */
                    }

                    //Returning "true" will cause the caller to decrease the Item we dropped in by 1.
                    @event.ReturnValue = true;
                    @event.ReturnEarly = true;
                    return;
                }

                //...and we're dropping in a "Honey"
                if (@event.ArgDroppedObject.Name.Equals("Honey"))
                {
                    //Tell the keg that we are now holding a "Mead".
                    @event.This.HeldObject = MakeKegMeadItem(@event);

                    //The "Probe" parameter tells us if the caller is just asking what's inside (not setting it). 
                    //When this is false, we need to start our cooking timer.
                    if (!@event.Probe)
                    {
                        //We reset the name here (just because that's what StardewValley does; I'm not convinced it's needed).
                        @event.This.HeldObject.Name = "Mead";

                        //Audio not implemented yet.
                        //Game1.playSound("Ship");
                        //Game1.playSound("bubbles");

                        //Set how long until this object is fully cooked. The smallest you can (reasonably) set this to is "10", since it updates
                        // on 10-minute intervals (like most things in the game).
                        @event.This.MinutesUntilReady = 1900;

                        //"Drop-In" animation not implemented yet.
                        /*
                        @event.ArgWho.CurrentLocation.TemporarySprites.Add(new TemporaryAnimatedSprite(@event.Root.Animations, new Rectangle(256, 1856, 64, 128), 80f, 6, 999999, this.tileLocation * (float)Game1.tileSize + new Vector2(0f, (float)(-(float)Game1.tileSize * 2)), false, false, (this.tileLocation.Y + 1f) * (float)Game1.tileSize / 10000f + 0.0001f, 0f, Color.Yellow * 0.75f, 1f, 0f, 0f, 0f, false)
                        {
                            alphaFade = 0.005f
                        });
                        */
                    }

                    //Returning "true" will cause the caller to decrease the Item we dropped in by 1.
                    @event.ReturnValue = true;
                    @event.ReturnEarly = true;
                    return;
                }
            }
        }


        //The UpdateCallback(TryToAddDrinkBuffCallbackEvent) event is called when we are drinking somethign and 
        // the game is trying to determine if it should give us a Buff (where "Tipsy" is considered a buff).
        //If we don't hook this, drinking the Vodka/Mead will restore health and energy, but it won't cause us to get
        // Tipsy. That is because the original function checks if the name is "Beer" or "Wine" (or "Ale"). One way to 
        // solve this is to rename "Mead" to "Mead (Beer)", but a better way is to handle this callback.
        [Subscribe]
        public void UpdateCallback(TryToAddDrinkBuffCallbackEvent @event)
        {
            //As always, do nothing if we're disabled.
            if (!ModConfig.EnablePlugin)
            {
                return;
            }

            //The @event.Buff is the current Buff we are considering (it is normally just a health buff, and a buff to "Stats").
            //We can check the Source, and apply another buff if that source is "Mead" or "Vodka".
            if (@event.Buff.Source.Contains("Mead") || @event.Buff.Source.Contains("Vodka"))
            {
                //Here, we use the BuffDelegate. This *would* cause our save files to crash... except that buffs are never saved.
                //Alternatively, you could use the Activator to make an actual Buff (not just a Proxy Buff).
                //Here, "17" is the magic buff code for "Tipsy".
                Buff buff = @event.Proxy<BuffAccessor, Buff>(new BuffDelegate(17));

                //We want Vodka tipsiness to last a lot longer.
                if (@event.Buff.Source.Contains("Vodka"))
                {
                    buff.MillisecondsDuration *= 10;
                }

                //Finally, we add it to the current "BuffsDisplay", which is sufficient for StardewValley to start tracking it.
                @event.BuffsDisplay.AddOtherBuff(buff);
            }

            //Note that we DO NOT set @event.ReturnEarly; we want the original function to continue processing the underlying buff like normal.
        }


        /////////////////////////////////////////////////////////////////////////////////////////////
        /// Helper functions
        /////////////////////////////////////////////////////////////////////////////////////////////


        private static ObjectItem MakeKegVodkaItem(PreObjectDropInActionEvent @event)
        {
            //NOTE: It should be possible to use the ObjectDelegate that's commented out below (and indeed, it appears to work!), but 
            //      the XmlSerializer gets hung up on serializing a proxy object. So, we take the original StardewValley type and clone it.
            //      This is kind of annoying, but much, MUCH easier than writing a hook into the XmlSerializer (I tried!)
            var temp = (ObjectAccessor)Activator.CreateInstance(@event.ArgDroppedObject.Underlying.GetType(), new object[] { Vector2.Zero, 792, "Vodka", false, true, false, false });
            return new ObjectItem(@event.ArgDroppedObject.Parent, temp);
            //return @event.Proxy<ObjectAccessor, ObjectItem>(new ObjectDelegate(Vector2.Zero, 792, "Vodka", false, true, false, false));
        }
        private static ObjectItem MakeKegMeadItem(PreObjectDropInActionEvent @event)
        {
            var temp = (ObjectAccessor)Activator.CreateInstance(@event.ArgDroppedObject.Underlying.GetType(), new object[] { Vector2.Zero, 793, "Mead", false, true, false, false });
            return new ObjectItem(@event.ArgDroppedObject.Parent, temp);
            //return @event.Proxy<ObjectAccessor, ObjectItem>(new ObjectDelegate(Vector2.Zero, 793, "Mead", false, true, false, false));
        }

        private static Dictionary<int, string> ReadTabbedObjectDictionary(string path, string keyName)
        {
            //Returns null on error.
            Dictionary<int, string> res = new Dictionary<int, string>();
            System.IO.StreamReader file = new System.IO.StreamReader(path);
            string line;
            while ((line = file.ReadLine()) != null)
            {
                string[] parts = line.Split('\t');
                if (parts.Length != 2)
                {
                    Console.WriteLine("Error: could not override " + keyName + " due to a corrupt line: " + line);
                    return null;
                }
                int key;
                if (!Int32.TryParse(parts[0], out key))
                {
                    Console.WriteLine("Error: could not override " + keyName + " due to an invalid integer key: " + parts[0]);
                    return null;
                }
                res[key] = parts[1];
            }

            return res;
        }
    }

    //Our config class.
    public class Config
    {
        public bool EnablePlugin { get; set; }
    }

    /****
    Copyright 2016 S'orlok Reaves (Seth N. Hetu)

    All rights reserved.

    Redistribution and use in source and binary forms, with or without
    modification, are permitted provided that the following conditions are met:
    1. Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer.
    2. Redistributions in binary form must reproduce the above copyright
       notice, this list of conditions and the following disclaimer in the
       documentation and/or other materials provided with the distribution.
    3. All advertising materials mentioning features or use of this software
       must display the following acknowledgement:
       This product includes software developed by the <organization>.
    4. Neither the name of the <organization> nor the
       names of its contributors may be used to endorse or promote products
       derived from this software without specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY <COPYRIGHT HOLDER> ''AS IS'' AND ANY
    EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
    WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED. IN NO EVENT SHALL <COPYRIGHT HOLDER> BE LIABLE FOR ANY
    DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
    (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
    LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
    ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
    (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
    SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
    ****/
}
