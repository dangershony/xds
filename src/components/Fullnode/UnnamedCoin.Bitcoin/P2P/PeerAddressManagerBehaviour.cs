﻿using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin.Protocol;
using UnnamedCoin.Bitcoin.Connection;
using UnnamedCoin.Bitcoin.P2P.Peer;
using UnnamedCoin.Bitcoin.P2P.Protocol;
using UnnamedCoin.Bitcoin.P2P.Protocol.Behaviors;
using UnnamedCoin.Bitcoin.P2P.Protocol.Payloads;
using UnnamedCoin.Bitcoin.Utilities;
using UnnamedCoin.Bitcoin.Utilities.Extensions;

namespace UnnamedCoin.Bitcoin.P2P
{
    /// <summary>
    ///     Behaviour implementation that encapsulates <see cref="IPeerAddressManager" />.
    ///     <para>
    ///         Subscribes to state change events from <see cref="INetworkPeer" /> and relays connection and handshake attempts
    ///         to
    ///         the <see cref="IPeerAddressManager" /> instance.
    ///     </para>
    /// </summary>
    public sealed class PeerAddressManagerBehaviour : NetworkPeerBehavior
    {
        /// <summary>The maximum amount of addresses per addr payload. </summary>
        /// <remarks><see cref="https://en.bitcoin.it/wiki/Protocol_documentation#addr" />.</remarks>
        const int MaxAddressesPerAddrPayload = 1000;

        /// <summary>Provider of time functions.</summary>
        readonly IDateTimeProvider dateTimeProvider;

        /// <summary>Instance logger.</summary>
        readonly ILogger logger;

        /// <summary>Builds loggers.</summary>
        readonly ILoggerFactory loggerFactory;

        /// <summary>Peer address manager instance, see <see cref="IPeerAddressManager" />.</summary>
        readonly IPeerAddressManager peerAddressManager;

        readonly IPeerBanning peerBanning;

        /// <summary>Flag to make sure <see cref="GetAddrPayload" /> is only sent once.</summary>
        /// TODO how does it help against peer reconnecting to reset the flag?
        bool addrPayloadSent;

        public PeerAddressManagerBehaviour(IDateTimeProvider dateTimeProvider, IPeerAddressManager peerAddressManager,
            IPeerBanning peerBanning, ILoggerFactory loggerFactory)
        {
            Guard.NotNull(dateTimeProvider, nameof(dateTimeProvider));
            Guard.NotNull(peerAddressManager, nameof(peerAddressManager));
            Guard.NotNull(peerAddressManager, nameof(peerBanning));
            Guard.NotNull(peerAddressManager, nameof(loggerFactory));

            this.dateTimeProvider = dateTimeProvider;
            this.logger = loggerFactory.CreateLogger(GetType().FullName, $"[{GetHashCode():x}] ");
            this.loggerFactory = loggerFactory;
            this.peerBanning = peerBanning;
            this.Mode = PeerAddressManagerBehaviourMode.AdvertiseDiscover;
            this.peerAddressManager = peerAddressManager;
            this.addrPayloadSent = false;
        }

        /// <summary>See <see cref="PeerAddressManagerBehaviourMode" /> for the different modes and their explanations.</summary>
        public PeerAddressManagerBehaviourMode Mode { get; set; }


        protected override void AttachCore()
        {
            this.AttachedPeer.StateChanged.Register(OnStateChangedAsync);
            this.AttachedPeer.MessageReceived.Register(OnMessageReceivedAsync);

            if ((this.Mode & PeerAddressManagerBehaviourMode.Discover) != 0)
                if (this.AttachedPeer.State == NetworkPeerState.Connected)
                    this.peerAddressManager.PeerConnected(this.AttachedPeer.PeerEndPoint,
                        this.dateTimeProvider.GetUtcNow());
        }

        async Task OnMessageReceivedAsync(INetworkPeer peer, IncomingMessage message)
        {
            try
            {
                if ((this.Mode & PeerAddressManagerBehaviourMode.Advertise) != 0)
                {
                    if (message.Message.Payload is GetAddrPayload)
                    {
                        if (!peer.Inbound)
                        {
                            this.logger.LogDebug("Outbound peer sent {0}. Not replying to avoid fingerprinting attack.",
                                nameof(GetAddrPayload));
                            return;
                        }

                        if (this.addrPayloadSent)
                        {
                            this.logger.LogDebug(
                                "Multiple GetAddr requests from peer. Not replying to avoid fingerprinting attack.");
                            return;
                        }

                        var endPoints = this.peerAddressManager.PeerSelector
                            .SelectPeersForGetAddrPayload(MaxAddressesPerAddrPayload).Select(p => p.Endpoint);
                        var addressPayload = new AddrPayload(endPoints.Select(p => new NetworkAddress(p)).ToArray());

                        await peer.SendMessageAsync(addressPayload).ConfigureAwait(false);

                        this.logger.LogDebug("Sent address payload following GetAddr request.");

                        this.addrPayloadSent = true;
                    }

                    if (message.Message.Payload is PingPayload || message.Message.Payload is PongPayload)
                        if (peer.State == NetworkPeerState.HandShaked)
                            this.peerAddressManager.PeerSeen(peer.PeerEndPoint, this.dateTimeProvider.GetUtcNow());
                }

                if ((this.Mode & PeerAddressManagerBehaviourMode.Discover) != 0)
                    if (message.Message.Payload is AddrPayload addr)
                    {
                        if (addr.Addresses.Length > MaxAddressesPerAddrPayload)
                        {
                            // Not respecting the protocol.
                            this.peerBanning.BanAndDisconnectPeer(peer.PeerEndPoint,
                                $"Protocol violation: addr payload size is limited by {MaxAddressesPerAddrPayload} entries.");

                            this.logger.LogTrace("(-)[PROTOCOL_VIOLATION]");
                            return;
                        }

                        this.peerAddressManager.AddPeers(addr.Addresses.Select(a => a.Endpoint),
                            peer.RemoteSocketAddress);
                    }
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task OnStateChangedAsync(INetworkPeer peer, NetworkPeerState previousState)
        {
            if ((this.Mode & PeerAddressManagerBehaviourMode.Discover) != 0)
                if (peer.State == NetworkPeerState.HandShaked)
                    this.peerAddressManager.PeerHandshaked(peer.PeerEndPoint, this.dateTimeProvider.GetUtcNow());

            if (peer.Inbound && peer.State == NetworkPeerState.HandShaked &&
                (this.Mode == PeerAddressManagerBehaviourMode.Advertise ||
                 this.Mode == PeerAddressManagerBehaviourMode.AdvertiseDiscover))
            {
                this.logger.LogDebug("[INBOUND] {0}:{1}, {2}:{3}, {4}:{5}", nameof(peer.RemoteSocketAddress),
                    peer.RemoteSocketAddress, nameof(peer.RemoteSocketEndpoint), peer.RemoteSocketEndpoint,
                    nameof(peer.RemoteSocketPort), peer.RemoteSocketPort);
                this.logger.LogDebug("[INBOUND] {0}:{1}, {2}:{3}", nameof(peer.PeerVersion.AddressFrom),
                    peer.PeerVersion?.AddressFrom, nameof(peer.PeerVersion.AddressReceiver),
                    peer.PeerVersion?.AddressReceiver);
                this.logger.LogDebug("[INBOUND] {0}:{1}", nameof(peer.PeerEndPoint), peer.PeerEndPoint);

                IPEndPoint inboundPeerEndPoint = null;

                // Use AddressFrom if it is not a Loopback address as this means the inbound node was configured with a different external endpoint.
                if (!peer.PeerVersion.AddressFrom.Match(new IPEndPoint(IPAddress.Loopback,
                    this.AttachedPeer.Network.DefaultPort)))
                    inboundPeerEndPoint = peer.PeerVersion.AddressFrom;
                else
                    // If it is a Loopback address use PeerEndpoint but combine it with the AdressFrom's port as that is the
                    // other node's listening port.
                    inboundPeerEndPoint = new IPEndPoint(peer.PeerEndPoint.Address, peer.PeerVersion.AddressFrom.Port);

                this.logger.LogDebug("{0}", inboundPeerEndPoint);

                this.peerAddressManager.AddPeer(inboundPeerEndPoint, IPAddress.Loopback);
            }

            return Task.CompletedTask;
        }


        protected override void DetachCore()
        {
            this.AttachedPeer.MessageReceived.Unregister(OnMessageReceivedAsync);
            this.AttachedPeer.StateChanged.Unregister(OnStateChangedAsync);
        }


        public override object Clone()
        {
            return new PeerAddressManagerBehaviour(this.dateTimeProvider, this.peerAddressManager, this.peerBanning,
                this.loggerFactory) {Mode = this.Mode};
        }
    }

    /// <summary>
    ///     Specifies how messages related to network peer discovery are handled.
    /// </summary>
    [Flags]
    public enum PeerAddressManagerBehaviourMode
    {
        /// <summary>Do not advertise nor discover new peers.</summary>
        None = 0,

        /// <summary>Only advertise known peers.</summary>
        Advertise = 1,

        /// <summary>Only discover peers.</summary>
        Discover = 2,

        /// <summary>Advertise known peer and discover peer.</summary>
        AdvertiseDiscover = 3
    }
}