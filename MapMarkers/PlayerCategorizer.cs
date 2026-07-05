using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace VideoSyncPrototype.MapMarkers;

/// <summary>
/// Walks the object table and buckets each visible player into friend / FC / everyone,
/// honoring the user's category toggles. Mirrors MiniMappingway's approach: friends come
/// from the character's status flag, FC members from comparing Free Company tags to the
/// local player's. A player is assigned the highest-priority category that is enabled.
/// </summary>
internal sealed class PlayerCategorizer
{
    /// <summary>Returns every player the user asked to see, already tagged with a color.</summary>
    public unsafe IReadOnlyList<CategorizedPlayer> Collect(Configuration config)
    {
        var results = new List<CategorizedPlayer>();

        var local = Plugin.ObjectTable.LocalPlayer;
        if (local is null)
        {
            return results;
        }

        var localFcTag = ReadFcTag((Character*)local.Address);

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter player || player.Address == local.Address)
            {
                continue;
            }

            var name = player.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var character = (Character*)player.Address;

            // Each player belongs to exactly one bucket, chosen by priority: friend, then
            // FC-mate, then everyone-else. That bucket's toggle is the ONLY thing that shows
            // or hides them — so unchecking "Friends" hides your friends even though most of
            // them are also in your FC (otherwise they'd stubbornly reappear as FC dots).
            if (character->IsFriend)
            {
                if (config.MarkShowFriends)
                {
                    results.Add(new CategorizedPlayer(name, player.Position, MarkerCategory.Friend, config.MarkFriendColor));
                }
            }
            else if (localFcTag.Length > 0 && TagsMatch(localFcTag, ReadFcTag(character)))
            {
                if (config.MarkShowFcMembers)
                {
                    results.Add(new CategorizedPlayer(name, player.Position, MarkerCategory.FreeCompany, config.MarkFcColor));
                }
            }
            else if (config.MarkShowEveryone)
            {
                results.Add(new CategorizedPlayer(name, player.Position, MarkerCategory.Everyone, config.MarkEveryoneColor));
            }
        }

        return results;
    }

    // The FC tag is a fixed byte buffer on the character; trim it at the first null so two
    // members compare equal regardless of trailing padding. An empty result means "no FC".
    private static unsafe byte[] ReadFcTag(Character* character)
    {
        if (character == null)
        {
            return [];
        }

        var span = character->FreeCompanyTag;
        var length = 0;
        while (length < span.Length && span[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            return [];
        }

        var tag = new byte[length];
        for (var i = 0; i < length; i++)
        {
            tag[i] = span[i];
        }

        return tag;
    }

    private static bool TagsMatch(byte[] left, byte[] right)
    {
        return right.Length > 0 && ((ReadOnlySpan<byte>)left).SequenceEqual(right);
    }
}
