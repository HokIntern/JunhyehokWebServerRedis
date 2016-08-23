using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Junhaehok;
using static Junhaehok.Packet;
using static Junhaehok.HhhHelper;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using System.Net.WebSockets;

namespace JunhyehokWebServerRedis
{
    class ReceiveHandle
    {
        ClientHandle client;
        Packet recvPacket;
        bool updateMMF;
        bool isBackend;

        public static Socket backend;
        public static RedisHandlerForFE redis;
        static string mmfName;
        static Dictionary<string, long> awaitingInit;
        static Dictionary<long, ClientHandle> clients;
        static Dictionary<int, Room> rooms;
        static MemoryMappedFile mmf;
        readonly Header NoResponseHeader = new Header(ushort.MaxValue, 0);
        readonly Packet NoResponsePacket = new Packet(new Header(ushort.MaxValue, 0), null);

        public ReceiveHandle(Socket backendSocket, RedisHandlerForFE redisHandler, string mmfNombre)
        {
            backend = backendSocket;
            redis = redisHandler;
            mmfName = mmfNombre;
            mmf = MemoryMappedFile.OpenExisting(mmfName);
            awaitingInit = new Dictionary<string, long>();
            clients = new Dictionary<long, ClientHandle>();
            rooms = new Dictionary<int, Room>();
        }

        public ReceiveHandle(ClientHandle client, Packet recvPacket)
        {
            this.client = client;
            this.recvPacket = recvPacket;
            updateMMF = false;
            isBackend = false;
        }
        public ReceiveHandle(BackendHandle backend, Packet recvPacket)
        {
            //should never to this.backend = backend
            //because this constructor is shared with Agent
            //and it is assumed that there is nothing to send back to agents
            //so never do this.backend = backend
            this.client = null;
            this.recvPacket = recvPacket;
            updateMMF = false;
            isBackend = true;
        }

        public static void RemoveClient(ClientHandle client, bool signout)
        {
            if (client.Status == ClientHandle.State.Room || client.Status == ClientHandle.State.Lobby)
            {
                if (client.Status == ClientHandle.State.Room)
                {
                    try { redis.LeaveRoom(client.UserId, client.RoomId); }
                    catch (Exception) { Console.WriteLine("ERROR: Redis operation failed (LeaveRoom)"); }

                    Room requestedRoom;
                    lock (rooms)
                    {
                        if (!rooms.TryGetValue(client.RoomId, out requestedRoom))
                            Console.WriteLine("ERROR: REMOVECLIENT - room doesn't exist {0}", client.RoomId);
                        else
                        {
                            requestedRoom.RemoveClient(client);

                            //destroy room is no one is in the room
                            if (requestedRoom.Clients.Count == 0)
                            {
                                rooms.Remove(requestedRoom.RoomId);
                                try { redis.DestroyRoom(requestedRoom.RoomId); }
                                catch (Exception) { Console.WriteLine("ERROR: Redis operation failed (DestroyRoom)"); }
                            }
                        }
                    }
                    client.Status = ClientHandle.State.Lobby;
                }

                bool clientExists = false;
                lock (clients)
                {
                    clientExists = clients.ContainsKey(client.UserId);
                    clients.Remove(client.UserId);
                }
                if (signout && clientExists)
                {
                    try { redis.SignOut(client.UserId); }
                    catch (Exception) { Console.WriteLine("ERROR: Redis operation failed (Signout)"); }
                }
                UpdateMMF();
                client.Status = ClientHandle.State.Offline;
            }
            else
                Console.WriteLine("ERROR: REMOVECLIENT - you messed up");
        }
        //========================================CONNECTION_PASS 650=============================================
        //========================================CONNECTION_PASS 650=============================================
        //========================================CONNECTION_PASS 650=============================================
        public Packet ResponseConnectionPass(Packet recvPacket)
        {
            FBConnectionPassResponse fbConnectionPassResp = (FBConnectionPassResponse)Serializer.ByteToStructure(recvPacket.data, typeof(FBConnectionPassResponse));
            char[] cookieChar = fbConnectionPassResp.cookie;
            string cookie = new string(cookieChar);
            lock (awaitingInit)
            {
                if (awaitingInit.ContainsKey(cookie))
                    awaitingInit[cookie] = recvPacket.header.uid;
                else
                    awaitingInit.Add(cookie, recvPacket.header.uid);
            }

            return NoResponsePacket;
        }
        //===========================================INITIALIZE 250==============================================
        //===========================================INITIALIZE 250==============================================
        //===========================================INITIALIZE 250==============================================
        public Packet ResponseInitialize(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;

            CFInitializeRequest cfInitializeReq = (CFInitializeRequest)Serializer.ByteToStructure(recvPacket.data, typeof(CFInitializeRequest));
            char[] cookieChar = cfInitializeReq.cookie;
            string cookie = new string(cookieChar);
            long uid;
            bool authorized = false;
            lock (awaitingInit)
            {
                if (awaitingInit.TryGetValue(cookie, out uid))
                {
                    awaitingInit.Remove(cookie);
                    authorized = true;
                }
            }
            if (authorized)
            {
                client.Status = ClientHandle.State.Lobby;
                client.UserId = uid;
                client.CookieChar = cookieChar;
                lock (clients)
                    clients.Add(client.UserId, client);
                updateMMF = true;

                returnData = null;
                returnHeader = new Header(Code.INITIALIZE_SUCCESS, 0);
                response = new Packet(returnHeader, returnData);

                Header backendReqHeader = new Header(Code.CONNECTION_PASS_SUCCESS, 0, client.UserId);
                Packet backendReqPacket = new Packet(backendReqHeader, null);
                backend.SendBytes(backendReqPacket);

                client.initFailCounter = 0;
            }
            else
            {
                returnData = null;
                returnHeader = new Header(Code.INITIALIZE_FAIL, 0);
                response = new Packet(returnHeader, returnData);

                client.initFailCounter++;
            }

            return response;
        }
        //==============================================CREATE 500===============================================
        //==============================================CREATE 500===============================================
        //==============================================CREATE 500===============================================
        public Packet ResponseCreate(Packet recvPacket)
        {
            Packet response;
            int roomId = -1;
            try { roomId = redis.CreateRoom(); }
            catch (Exception) { Console.WriteLine("ERROR: Redis operation failed (CreateRoom)"); }
            if (-1 != roomId)
                response = ResponseCreateSuccess(roomId);
            else
                response = ResponseCreateFail();

            return response;
        }
        //=======================================CREATE_SUCCESS 502==============================================
        //=======================================CREATE_SUCCESS 502==============================================
        //=======================================CREATE_SUCCESS 502==============================================
        public Packet ResponseCreateSuccess(int roomId)
        {
            Packet response;
            Header returnHeader;

            //add room to dictionary
            Room requestedRoom = new Room(roomId);
            lock (rooms)
            {
                rooms.Add(roomId, requestedRoom);
                //IMPORTANT: have to add client to room and change its status because
                //client side assumes join when FE sends CREATE_SUCCESS
                //so no choice but to do it here
                requestedRoom.AddClient(client);
                client.Status = ClientHandle.State.Room;
                client.RoomId = roomId;
            }

            //send CREATE_ROOM_SUCCESS back to client
            CFRoomCreateResponse cfRoomCreateResp;
            cfRoomCreateResp.roomNum = roomId;
            byte[] cfRoomCreateRespBytes = Serializer.StructureToByte(cfRoomCreateResp);
            returnHeader = new Header(Code.CREATE_ROOM_SUCCESS, (ushort)cfRoomCreateRespBytes.Length);
            response = new Packet(returnHeader, cfRoomCreateRespBytes);

            //send JOIN to Backend
            try { redis.JoinRoom(client.UserId, client.RoomId); }
            catch (Exception) { Console.WriteLine("ERROR: Redis operation failed (JoinRoom)"); }
            
            updateMMF = true;
            return response;
        }
        //=======================================CREATE_FAIL 505=================================================
        //=======================================CREATE_FAIL 505=================================================
        //=======================================CREATE_FAIL 505=================================================
        public Packet ResponseCreateFail()
        {
            Header returnHeader = new Header(Code.CREATE_ROOM_FAIL, 0);
            Packet response = new Packet(returnHeader, null);
            return response;
        }
        //================================================JOIN 600===============================================
        //================================================JOIN 600===============================================
        //================================================JOIN 600===============================================
        public Packet ResponseJoin(Packet recvPacket)
        {
            Packet response;

            CFRoomJoinRequest cfRoomJoinReq = (CFRoomJoinRequest)Serializer.ByteToStructure(recvPacket.data, typeof(CFRoomJoinRequest));
            int roomId = cfRoomJoinReq.roomNum;

            Room requestedRoom;
            bool roomExists = false;
            lock (rooms)
            {
                if (rooms.TryGetValue(roomId, out requestedRoom))
                {
                    roomExists = true;
                    requestedRoom.AddClient(client);
                    client.Status = ClientHandle.State.Room;
                    client.RoomId = roomId;

                    try { redis.JoinRoom(client.UserId, roomId); }
                    catch (Exception) { Console.WriteLine("ERROR: Redis operation failed (JoinRoom)"); }
                }
            }

            if (roomExists)
                response = new Packet(new Header(Code.JOIN_SUCCESS, 0), null);
            else
            {
                FBRoomJoinRequest fbRoomJoinReq;
                fbRoomJoinReq.cookie = client.CookieChar;
                fbRoomJoinReq.roomNum = roomId;
                byte[] fbRoomJoinReqBytes = Serializer.StructureToByte(fbRoomJoinReq);

                Header backendReqHeader = new Header(Code.JOIN, (ushort)fbRoomJoinReqBytes.Length, client.UserId);
                Packet backendReqPacket = new Packet(backendReqHeader, fbRoomJoinReqBytes);
                backend.SendBytes(backendReqPacket);

                response = NoResponsePacket;
            }

            updateMMF = true;
            return response;
        }
        //==========================================JOIN_FAIL 605===============================================
        //==========================================JOIN_FAIL 605===============================================
        //==========================================JOIN_FAIL 605===============================================
        public Packet ResponseJoinFail(Packet recvPacket)
        {
            return ForwardPacketWithUserIdUpdated(recvPacket);
        }
        //==========================================JOIN_FULL_FAIL 605==========================================
        //==========================================JOIN_FULL_FAIL 605==========================================
        //==========================================JOIN_FULL_FAIL 605==========================================
        public Packet ResponseJoinFullFail(Packet recvPacket)
        {
            return ForwardPacketWithUserIdUpdated(recvPacket);
        }
        //==========================================JOIN_NULL_FAIL 605==========================================
        //==========================================JOIN_NULL_FAIL 605==========================================
        //==========================================JOIN_NULL_FAIL 605==========================================
        public Packet ResponseJoinNullFail(Packet recvPacket)
        {
            return ForwardPacketWithUserIdUpdated(recvPacket);
        }
        //========================================JOIN_REDIRECT 605=============================================
        //========================================JOIN_REDIRECT 605=============================================
        //========================================JOIN_REDIRECT 605=============================================
        public Packet ResponseJoinRedirect(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;

            ClientHandle clientToSend = GetClientFromUid(recvPacket.header.uid);
            if (null != clientToSend)
            {
                returnHeader = recvPacket.header;
                returnHeader.uid = 0;
                response = new Packet(returnHeader, recvPacket.data);
                clientToSend.SendPacket(response);

                lock (clients)
                    clients.Remove(clientToSend.UserId);
                updateMMF = true;
                //clientToSend.CloseConnection();

                //send nothing back to Backend
                response = NoResponsePacket;
            }
            else
                response = NoResponsePacket;

            return response;
        }
        //==============================================LEAVE 600===============================================
        //==============================================LEAVE 600===============================================
        //==============================================LEAVE 600===============================================
        public Packet ResponseLeave(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;
            Room requestedRoom;

            if (client.Status != ClientHandle.State.Room)
            {
                //can't leave if client is in the lobby. must be in a room
                returnHeader = new Header(Code.LEAVE_ROOM_FAIL, 0);
                returnData = null;
            }
            else
            {
                lock (rooms)
                {
                    if (!rooms.TryGetValue(client.RoomId, out requestedRoom))
                    {
                        Console.WriteLine("ERROR: Client is in a room that doesn't exist. WTF you fucked up.");
                        returnHeader = new Header(Code.LEAVE_ROOM_FAIL, 0);
                        returnData = null;
                    }
                    else
                    {
                        requestedRoom.RemoveClient(client);
                        try { redis.LeaveRoom(client.UserId, client.RoomId); }
                        catch (Exception) { Console.WriteLine("ERROR: Redis operation failed (LeaveRoom)"); }
                        client.RoomId = 0;
                        client.Status = ClientHandle.State.Lobby;

                        //destroy room if no one is in the room
                        if (requestedRoom.Clients.Count == 0)
                        {
                            rooms.Remove(requestedRoom.RoomId);

                            //send destroy room to backend
                            try { redis.DestroyRoom(requestedRoom.RoomId); }
                            catch (Exception) { Console.WriteLine("ERROR: Redis operation failed (DestroyRoom)"); }
                        }

                        returnHeader = new Header(Code.LEAVE_ROOM_SUCCESS, 0);
                        returnData = null;
                    }
                }
            }

            updateMMF = true;
            response = new Packet(returnHeader, returnData);
            return response;
        }
        //==============================================LIST 400===============================================
        //==============================================LIST 400===============================================
        //==============================================LIST 400===============================================
        public Packet ResponseList(Packet recvPacket)
        {
            Packet response;

            byte[] listBytes = { };
            try { listBytes = redis.GetRoomList(); }
            catch (Exception) { Console.WriteLine("ERROR: Redis operation failed (GetRoomList)"); }
            response = new Packet(new Header(Code.ROOM_LIST_SUCCESS, (ushort)listBytes.Length), listBytes);

            return response;
        }
        //================================================MSG 200===============================================
        //================================================MSG 200===============================================
        //================================================MSG 200===============================================
        public Packet ResponseMsg(Packet recvPacket)
        {
            Room requestedRoom;

            client.ChatCount++;
            //This send is to notify backend to increment chat count
            try { redis.MSG(client.UserId); }
            catch (Exception) { Console.WriteLine("ERROR: Redis operation failed (MSG)"); }

            lock (rooms)
            {
                if (!rooms.TryGetValue(client.RoomId, out requestedRoom))
                {
                    Console.WriteLine("ERROR: Msg - Room doesn't exist");
                    // room doesnt exist error
                }
                else
                {
                    foreach (ClientHandle peerClient in requestedRoom.Clients)
                        peerClient.SendPacket(recvPacket);
                }
            }

            return NoResponsePacket;
        }
        //=========================================UPDATE_USER 120==============================================
        //=========================================UPDATE_USER 120==============================================
        //=========================================UPDATE_USER 120==============================================
        public Packet ResponseUpdateUser(Packet recvPacket)
        {
            recvPacket.header.uid = client.UserId;
            backend.SendBytes(recvPacket);

            return NoResponsePacket;
        }
        //===================================UPDATE_USER_SUCCESS 120============================================
        //===================================UPDATE_USER_SUCCESS 120============================================
        //===================================UPDATE_USER_SUCCESS 120============================================
        public Packet ResponseUpdateUserSuccess(Packet recvPacket)
        {
            return ForwardPacketWithUserIdUpdated(recvPacket);
        }
        //======================================UPDATE_USER_FAIL 120============================================
        //======================================UPDATE_USER_FAIL 120============================================
        //======================================UPDATE_USER_FAIL 120============================================
        public Packet ResponseUpdateUserFail(Packet recvPacket)
        {
            return ForwardPacketWithUserIdUpdated(recvPacket);
        }
        //=========================================DELETE_USER 120==============================================
        //=========================================DELETE_USER 120==============================================
        //=========================================DELETE_USER 120==============================================
        public Packet ResponseDeleteUser(Packet recvPacket)
        {
            recvPacket.header.uid = client.UserId;
            backend.SendBytes(recvPacket);

            return NoResponsePacket;
        }
        //===================================DELETE_USER_SUCCESS 120============================================
        //===================================DELETE_USER_SUCCESS 120============================================
        //===================================DELETE_USER_SUCCESS 120============================================
        public Packet ResponseDeleteUserSuccess(Packet recvPacket)
        {
            RemoveClient(client, false);
            return ForwardPacketWithUserIdUpdated(recvPacket);
        }
        //======================================DELETE_USER_FAIL 120============================================
        //======================================DELETE_USER_FAIL 120============================================
        //======================================DELETE_USER_FAIL 120============================================
        public Packet ResponseDeleteUserFail(Packet recvPacket)
        {
            return ForwardPacketWithUserIdUpdated(recvPacket);
        }
        //=========================================SERVER_STOP 1270=============================================
        //=========================================SERVER_STOP 1270=============================================
        //=========================================SERVER_STOP 1270=============================================
        public Packet ResponseServerStop(Packet recvPacket)
        {
            if (IsAgent())
            {
                UpdateMMF(false);
                backend.Shutdown(SocketShutdown.Both);
                backend.Close();
                Environment.Exit(0);
            }
            return NoResponsePacket;
        }

        //=============================================SWITCH CASE============================================
        //=============================================SWITCH CASE============================================
        //=============================================SWITCH CASE============================================
        public Packet GetResponse()
        {
            Packet responsePacket = new Packet();

            string remoteHost="backend";
            string remotePort="backend";
            if (null != client)
            {
                remoteHost = client.remoteHost;
                remotePort = client.remotePort;
            }

            bool debug = true;

            if (debug && recvPacket.header.code != Code.HEARTBEAT && recvPacket.header.code != Code.HEARTBEAT_SUCCESS && recvPacket.header.code != ushort.MaxValue - 1)
            {
                Console.WriteLine("\n[Client] {0}:{1}", remoteHost, remotePort);
                Console.WriteLine("==RECEIVED: \n" + PacketDebug(recvPacket));
            }

            if (!IsAgent() && !isBackend && !HasInitialized())
                return new Packet(new Header(Code.INITIALIZE_FAIL, 0), null);

            switch (recvPacket.header.code)
            {
                //------------No action from client----------
                case ushort.MaxValue - 1:
                    responsePacket = new Packet(new Header(Code.HEARTBEAT, 0), null);
                    break;

                //--------CONNECTION_PASS-------
                case Code.CONNECTION_PASS:
                    responsePacket = ResponseConnectionPass(recvPacket);
                    break;

                //----------INITIALIZE----------
                case Code.INITIALIZE:
                    responsePacket = ResponseInitialize(recvPacket);
                    break;

                //------------CREATE------------
                case Code.CREATE_ROOM:
                    //CL -> FE side
                    responsePacket = ResponseCreate(recvPacket);
                    break;

                //------------HEARTBEAT------------
                case Code.HEARTBEAT:
                    //FE -> CL side
                    responsePacket = new Packet(new Header(Code.HEARTBEAT_SUCCESS, 0), null);
                    break;
                case Code.HEARTBEAT_SUCCESS:
                    //CL -> FE side
                    responsePacket = NoResponsePacket;
                    break;

                //------------JOIN------------
                case Code.JOIN:
                    //CL -> FE side
                    responsePacket = ResponseJoin(recvPacket);
                    break;
                case Code.JOIN_FULL_FAIL:
                    responsePacket = ResponseJoinFullFail(recvPacket);
                    break;
                case Code.JOIN_NULL_FAIL:
                    responsePacket = ResponseJoinNullFail(recvPacket);
                    break;
                case Code.JOIN_FAIL:
                    responsePacket = ResponseJoinFail(recvPacket);
                    break;
                case Code.JOIN_REDIRECT:
                    responsePacket = ResponseJoinRedirect(recvPacket);
                    break;

                //------------LEAVE------------
                case Code.LEAVE_ROOM:
                    //CL -> FE side
                    responsePacket = ResponseLeave(recvPacket);
                    break;

                //------------LIST------------
                case Code.ROOM_LIST:
                    //CL -> FE side
                    responsePacket = ResponseList(recvPacket);
                    break;

                //------------MSG------------
                case Code.MSG:
                    //CL <--> FE side
                    responsePacket = ResponseMsg(recvPacket);
                    break;

                //-----------SIGNOUT---------
                case Code.SIGNOUT:
                    RemoveClient(client, true);
                    responsePacket = new Packet(new Header(Code.SIGNOUT_SUCCESS, 0), null);
                    break;

                //---------UPDATE USER-------
                case Code.UPDATE_USER:
                    responsePacket = ResponseUpdateUser(recvPacket);
                    break;
                case Code.UPDATE_USER_SUCCESS:
                    responsePacket = ResponseUpdateUserSuccess(recvPacket);
                    break;
                case Code.UPDATE_USER_FAIL:
                    responsePacket = ResponseUpdateUserFail(recvPacket);
                    break;

                //---------DELETE USER-------
                case Code.DELETE_USER:
                    responsePacket = ResponseDeleteUser(recvPacket);
                    break;
                case Code.DELETE_USER_SUCCESS:
                    responsePacket = ResponseDeleteUserSuccess(recvPacket);
                    break;
                case Code.DELETE_USER_FAIL:
                    responsePacket = ResponseDeleteUserFail(recvPacket);
                    break;

                //--------SERVER STOP--------
                case Code.SERVER_STOP:
                    responsePacket = ResponseServerStop(recvPacket);
                    break;

                default:
                    if (debug)
                        Console.WriteLine("Unknown code: {0}\n", recvPacket.header.code);
                    break;
            }

            //===================Update MMF for IPC with agent==================
            if (updateMMF)
                UpdateMMF();

            /*
            //===============Build Response/Set Surrogate/Return================
            if (debug && responsePacket.header.code != ushort.MaxValue && responsePacket.header.code != Code.HEARTBEAT && responsePacket.header.code != Code.HEARTBEAT_SUCCESS)
            {
                Console.WriteLine("\n[Client] {0}:{1}", remoteHost, remotePort);
                Console.WriteLine("==SEND: \n" + PacketDebug(responsePacket));
            }
            */

            return responsePacket;
        }

        private ClientHandle GetClientFromUid(long uid)
        {
            ClientHandle clientToSend;
            if (!clients.TryGetValue(uid, out clientToSend))
            {
                Console.WriteLine("WARNING: GetClientFromUid failed - client no longer exists");
                return null;
            }
            return clientToSend;
        }
        private bool HasInitialized()
        {
            try
            {
                if (recvPacket.header.code == Code.INITIALIZE)
                    return true;
                return !(client.UserId == -1 || client.Cookie == null);
            }
            catch (Exception) { Console.WriteLine("ERROR: HasInitialized - Socket lost"); return false; }
        }
        public static void UpdateMMF(bool alive = true)
        {
            int clientCount = clients.Count;
            int roomCount = rooms.Count;
            AAServerInfoResponse aaServerInfoResp;
            aaServerInfoResp.alive = alive;
            aaServerInfoResp.userCount = alive == true ? clientCount : 0;
            aaServerInfoResp.roomCount = alive == true ? roomCount : 0;
            byte[] aaServerInfoRespBytes = Serializer.StructureToByte(aaServerInfoResp);

            Console.WriteLine("[MEMORYMAPPED FILE] Writing to MMF: ({0})...", mmfName);

            Mutex mutex = Mutex.OpenExisting("MMF_IPC" + mmfName);
            mutex.WaitOne();

            // Create Accessor to MMF
            using (var accessor = mmf.CreateViewAccessor(0, aaServerInfoRespBytes.Length))
            {
                // Write to MMF
                accessor.WriteArray<byte>(0, aaServerInfoRespBytes, 0, aaServerInfoRespBytes.Length);
            }
            mutex.ReleaseMutex();
        }

        private Packet ForwardPacketWithUserIdUpdated(Packet recvPacket)
        {
            Packet response;
            Header returnHeader;

            ClientHandle clientToSend = GetClientFromUid(recvPacket.header.uid);
            if (null != clientToSend)
            {
                returnHeader = recvPacket.header;
                returnHeader.uid = 0;
                response = new Packet(returnHeader, recvPacket.data);
                clientToSend.SendPacket(response);

                //send nothing back to Backend
                response = NoResponsePacket;
            }
            else
                response = NoResponsePacket;

            return response;
        }
        private bool IsAgent()
        {
            if (null == client) //must not be a ClientHandle. Agents are backendHandles
                return true;
            return false;                
        }
    }
}
