// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Gumps.CharCreation;

namespace ClassicUO.Agent
{
    internal sealed partial class CommandEngine
    {
        /// <summary>How long `newchar` waits for the creation screen after asking for it from character selection.</summary>
        private const int CHAR_CREATION_WAIT_MS = 3000;

        private const int CHAR_CREATION_POLL_MS = 100;

        /// <summary>The starting skills only a Samurai Empire account may pick (Bushido, Ninjitsu).</summary>
        private const int SKILL_BUSHIDO = 52;
        private const int SKILL_NINJITSU = 53;

        private void RegisterLogin()
        {
            // A fresh account has no character to play, and character creation is all client-side
            // gumps that `gumps`/`gumpresponse` cannot reach. This takes the steps a player clicks
            // through - appearance, profession, city - on the client's own CharCreationGump, so the
            // server receives exactly what the window would have sent.
            Register("newchar", "newchar <name> [profession]",
                "Create a character on the logged-in account (profession default warrior)", ctx =>
            {
                string name = ctx.Arg(0);
                string professionName = ctx.Arg(1) ?? "warrior";

                if (string.IsNullOrEmpty(name))
                {
                    ctx.Warn("usage: newchar <name> [profession]");

                    return;
                }

                if (!ctx.Game(_ => OpenCharCreation()))
                {
                    ctx.Fail("not at character selection or creation - log in to the account first");

                    return;
                }

                // From character selection the login scene builds the creation screen a frame later.
                for (int waited = 0; ctx.Game(_ => UIManager.GetGump<CharCreationGump>() == null); waited += CHAR_CREATION_POLL_MS)
                {
                    if (waited >= CHAR_CREATION_WAIT_MS || !ctx.Sleep(CHAR_CREATION_POLL_MS))
                    {
                        ctx.Fail("the character creation screen did not open");

                        return;
                    }
                }

                string summary = null;
                string error = ctx.Game(w => CreateCharacter(w, name, professionName, out summary));

                if (error != null)
                {
                    ctx.Fail(error);

                    return;
                }

                ctx.Print($"{summary} - the client logs in as soon as the server accepts it");
            });
        }

        /// <summary>True when the creation screen is open or has just been asked for. Game thread.</summary>
        private static bool OpenCharCreation()
        {
            if (UIManager.GetGump<CharCreationGump>() != null)
            {
                return true;
            }

            LoginScene scene = ClassicUO.Client.Game.GetScene<LoginScene>();

            if (scene == null || scene.CurrentLoginStep != LoginSteps.CharacterSelection)
            {
                return false;
            }

            scene.StartCharCreation();

            return true;
        }

        /// <summary>
        /// Names the creation screen's character, gives it the profession's stats and skills, picks
        /// the first starting city and sends the request. Returns why it could not, or null. Game thread.
        /// </summary>
        private static string CreateCharacter(World world, string name, string professionName, out string summary)
        {
            summary = null;

            CharCreationGump creation = UIManager.GetGump<CharCreationGump>();

            if (creation == null)
            {
                return "the character creation screen is not open";
            }

            int invalid = CreateCharAppearanceGump.Validate(name);

            if (invalid > 0)
            {
                return $"name '{name}' refused: {ClassicUO.Client.Game.UO.FileManager.Clilocs.GetString(invalid)}";
            }

            List<ProfessionInfo> professions = PickableProfessions().ToList();
            ProfessionInfo profession = professions.FirstOrDefault(p =>
                string.Equals(p.Name, professionName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.TrueName, professionName, StringComparison.OrdinalIgnoreCase));

            if (profession == null)
            {
                return $"no profession '{professionName}' - one of: {string.Join(", ", professions.Select(p => p.TrueName ?? p.Name))}";
            }

            if (NeedsSamuraiEmpire(profession) && (world.ClientFeatures.Flags & CharacterListFlags.CLF_SAMURAI_NINJA) == 0)
            {
                return $"{profession.Name} needs a Samurai Empire account";
            }

            // The appearance screen's own preview mobile (serial 1), already dressed in its defaults.
            if (world.Mobiles.Get(1) is not PlayerMobile character)
            {
                return "the creation screen has no character to dress";
            }

            CityInfo city = ClassicUO.Client.Game.GetScene<LoginScene>()?.GetCity(0);

            if (city == null)
            {
                return "the server sent no starting cities";
            }

            character.Name = name;
            creation.SetCharacter(character);
            creation.SetProfession(profession);
            creation.SetCity(city.Index);
            creation.CreateCharacter((byte)profession.DescriptionIndex);
            creation.IsVisible = false;

            summary = $"Creating {name}, {profession.Name}, starting in {city.City}";

            return null;
        }

        /// <summary>The preset professions the creation screen offers, categories opened up.</summary>
        private static IEnumerable<ProfessionInfo> PickableProfessions()
        {
            foreach (KeyValuePair<ProfessionInfo, List<ProfessionInfo>> entry in ClassicUO.Client.Game.UO.FileManager.Professions.Professions)
            {
                foreach (ProfessionInfo info in entry.Value != null ? entry.Value.Prepend(entry.Key) : new[] { entry.Key })
                {
                    if (info.Type == ProfessionLoader.PROF_TYPE.PROFESSION && info.DescriptionIndex > 0)
                    {
                        yield return info;
                    }
                }
            }
        }

        private static bool NeedsSamuraiEmpire(ProfessionInfo profession)
        {
            for (int i = 0; i < profession.SkillDefVal.GetLength(0); i++)
            {
                int skill = profession.SkillDefVal[i, 0];

                if (skill == SKILL_BUSHIDO || skill == SKILL_NINJITSU)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
