using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using fnecore.DMR;
using fnecore;
using fnecore.P25.kmm;

namespace WhackerLinkConsoleV2
{
    /// <summary>
    /// Represents the individual timeslot data status.
    /// </summary>
    public class SlotStatus
    {
        /// <summary>
        /// Rx Start Time
        /// </summary>
        public DateTime RxStart = DateTime.Now;

        /// <summary>
        /// 
        /// </summary>
        public uint RxSeq = 0;

        /// <summary>
        /// Rx RF Source
        /// </summary>
        public uint RxRFS = 0;
        /// <summary>
        /// Tx RF Source
        /// </summary>
        public uint TxRFS = 0;

        /// <summary>
        /// Rx Stream ID
        /// </summary>
        public uint RxStreamId = 0;
        /// <summary>
        /// Tx Stream ID
        /// </summary>
        public uint TxStreamId = 0;

        /// <summary>
        /// Rx TG ID
        /// </summary>
        public uint RxTGId = 0;
        /// <summary>
        /// Tx TG ID
        /// </summary>
        public uint TxTGId = 0;
        /// <summary>
        /// Tx Privacy TG ID
        /// </summary>
        public uint TxPITGId = 0;

        /// <summary>
        /// Rx Time
        /// </summary>
        public DateTime RxTime = DateTime.Now;
        /// <summary>
        /// Tx Time
        /// </summary>
        public DateTime TxTime = DateTime.Now;

        /// <summary>
        /// Rx Type
        /// </summary>
        public FrameType RxType = FrameType.TERMINATOR;

        /** DMR Data */
        /// <summary>
        /// Rx Link Control Header
        /// </summary>
        public LC DMR_RxLC = null;
        /// <summary>
        /// Rx Privacy Indicator Link Control Header
        /// </summary>
        public PrivacyLC DMR_RxPILC = null;
        /// <summary>
        /// Tx Link Control Header
        /// </summary>
        public LC DMR_TxHLC = null;
        /// <summary>
        /// Tx Privacy Link Control Header
        /// </summary>
        public PrivacyLC DMR_TxPILC = null;
        /// <summary>
        /// Tx Terminator Link Control
        /// </summary>
        public LC DMR_TxTLC = null;
    } // public class SlotStatus

    /// <summary>
    /// Implements a FNE system.
    /// </summary>
    public abstract partial class FneSystemBase : fnecore.FneSystemBase
    {
        public List<byte[]> processedChunks = new List<byte[]>();

        private Random rand;

        internal MainWindow mainWindow;

        // List of active calls
        private List<(uint, byte)> activeTalkgroups = new List<(uint, byte)>();

        /*
        ** Methods
        */

        /// <summary>
        /// Initializes a new instance of the <see cref="FneSystemBase"/> class.
        /// </summary>
        /// <param name="fne">Instance of <see cref="FneMaster"/> or <see cref="FnePeer"/></param>
        public FneSystemBase(FnePeer fne, MainWindow mainWindow) : base(fne, 0)
        {
            this.fne = fne;
            this.mainWindow = mainWindow;

            this.rand = new Random(Guid.NewGuid().GetHashCode());

            // hook logger callback
            this.fne.Logger = (LogLevel level, string message) =>
            {
                switch (level)
                {
                    case LogLevel.WARNING:
                        Console.WriteLine(message);
                        break;
                    case LogLevel.ERROR:
                        Console.WriteLine(message);
                        break;
                    case LogLevel.DEBUG:
                        Console.WriteLine(message);
                        break;
                    case LogLevel.FATAL:
                        Console.WriteLine(message);
                        break;
                    case LogLevel.INFO:
                    default:
                        Console.WriteLine(message);
                        break;
                }
            };
        }

        /// <summary>
        /// Stops the main execution loop for this <see cref="FneSystemBase"/>.
        /// </summary>
        public override void Stop()
        {
            base.Stop();
        }

        /// <summary>
        /// Callback used to process whether or not a peer is being ignored for traffic.
        /// </summary>
        /// <param name="peerId">Peer ID</param>
        /// <param name="srcId">Source Address</param>
        /// <param name="dstId">Destination Address</param>
        /// <param name="slot">Slot Number</param>
        /// <param name="callType">Call Type (Group or Private)</param>
        /// <param name="frameType">Frame Type</param>
        /// <param name="dataType">DMR Data Type</param>
        /// <param name="streamId">Stream ID</param>
        /// <returns>True, if peer is ignored, otherwise false.</returns>
        protected override bool PeerIgnored(uint peerId, uint srcId, uint dstId, byte slot, fnecore.CallType callType, FrameType frameType, DMRDataType dataType, uint streamId)
        {
            return false;
        }

        /// <summary>
        /// Event handler used to handle a peer connected event.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected override void PeerConnected(object sender, PeerConnectedEvent e)
        {
            return;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected override void KeyResponse(object sender, KeyResponseEvent e)
        {
            byte[] payload = e.Data.Skip(11).ToArray();

            //Console.WriteLine(FneUtils.HexDump(payload));

            if (e.MessageId == (byte)KmmMessageType.MODIFY_KEY_CMD)
            {
                mainWindow.KeyResponseReceived(e);
            }
        }

        /// <summary>
        /// Returns a new stream ID
        /// </summary>
        /// <returns></returns>
        public uint NewStreamId()
        {
            return (uint)rand.Next(int.MinValue, int.MaxValue);
        }

    } // public abstract partial class FneSystemBase : fnecore.FneSystemBase
}