using PacketDotNet;
using PacketDotNet.Utils;
using System;

namespace AlbionDataAvalonia.Network.Services;

/// <summary>
/// Finds the UDP datagram in a captured frame. VPN tunnels (Wintun, TUN, utun) hand the capture bare IP packets
/// under link types PacketDotNet can't parse, such as IPv4 and IPv6, so those are read from the IP header directly.
/// </summary>
public static class CapturedPacketParser
{
    public static UdpPacket? ExtractUdp(LinkLayers linkLayer, byte[] data)
    {
        switch (linkLayer)
        {
            case LinkLayers.Raw:
            case LinkLayers.RawLegacy:
            case LinkLayers.IPv4:
            case LinkLayers.IPv6:
                return ExtractUdpFromIp(data, 0);
            // A 4-byte address family comes before the IP header; its value differs between systems for IPv6.
            case LinkLayers.Null:
            case LinkLayers.Loop:
                return ExtractUdpFromIp(data, 4);
        }

        try
        {
            return Packet.ParsePacket(linkLayer, data).Extract<UdpPacket>();
        }
        catch (NotImplementedException)
        {
            return ExtractUdpFromIp(data, 0);
        }
    }

    /// <summary>Whether the link type carries bare IP packets, as VPN tunnel adapters do.</summary>
    public static bool IsTunnelLinkType(LinkLayers linkLayer) => linkLayer is LinkLayers.Raw or LinkLayers.RawLegacy
        or LinkLayers.IPv4 or LinkLayers.IPv6 or LinkLayers.Null or LinkLayers.Loop or LinkLayers.Ppp;

    private static UdpPacket? ExtractUdpFromIp(byte[] data, int offset)
    {
        if (data.Length <= offset)
        {
            return null;
        }

        var segment = new ByteArraySegment(data, offset, data.Length - offset);
        IPPacket? ip = (data[offset] >> 4) switch
        {
            4 => new IPv4Packet(segment),
            6 => new IPv6Packet(segment),
            _ => null
        };
        return ip?.Extract<UdpPacket>();
    }
}
