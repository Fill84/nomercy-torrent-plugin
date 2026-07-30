// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
//
// NoMercy MediaServer Automated Torrent Plugin 
// Created by Phillippe Pelzer https://github.com/Fill84
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Xml.Linq;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;

public static class RssFeedParser
{
    public static IReadOnlyList<RssItem> Parse(string xml)
    {
        XDocument document;

        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException error)
        {
            throw new IndexerException($"malformed feed XML: {error.Message}", error);
        }

        List<RssItem> items = [];

        foreach (XElement element in document.Descendants("item"))
        {
            string title = (string?)element.Element("title") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title))
                continue;

            XElement? enclosure = element.Element("enclosure");

            items.Add(
                new RssItem
                {
                    Title = title.Trim(),
                    Link = Trimmed(element.Element("link")),
                    Guid = Trimmed(element.Element("guid")),
                    Published = ParseDate(Trimmed(element.Element("pubDate"))),
                    Categories = element
                        .Elements("category")
                        .Select(category => ((string)category).Trim())
                        .Where(category => category.Length > 0)
                        .ToArray(),
                    EnclosureUrl = Trimmed(enclosure?.Attribute("url")),
                    EnclosureLength = ParseLength(Trimmed(enclosure?.Attribute("length"))),
                    EnclosureType = Trimmed(enclosure?.Attribute("type")),
                }
            );
        }

        return items;
    }

    private static string? Trimmed(XElement? element)
    {
        string? value = ((string?)element)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? Trimmed(XAttribute? attribute)
    {
        string? value = ((string?)attribute)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static DateTimeOffset? ParseDate(string? text) =>
        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed
        )
            ? parsed
            : null;

    private static long ParseLength(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long length)
            ? length
            : 0L;
}
