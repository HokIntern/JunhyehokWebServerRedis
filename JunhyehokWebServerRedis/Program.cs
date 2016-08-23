using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Junhaehok;
using static Junhaehok.HhhHelper;
using System.Web;
using System.Net.WebSockets;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace JunhyehokWebServerRedis
{
    class Program
    {
        static void Main(string[] args)
        {
            string host = null;     //Default
            string clientPort = "30000";  //Default
            string mmfName = "JunhyehokMmf"; //Default

            //=========================GET ARGS=================================
            if (args.Length == 0)
            {
                Console.WriteLine("Format: JunhyehokServer -cp [client port] -mmf [MMF name]");
                Environment.Exit(0);
            }

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help":
                        Console.WriteLine("Format: JunhyehokServer -cp [client port] -mmf [MMF name]");
                        Environment.Exit(0);
                        break;
                    case "-mmf":
                        mmfName = args[++i];
                        break;
                    case "-cp":
                        clientPort = args[++i];
                        break;
                    default:
                        Console.Error.WriteLine("ERROR: incorrect inputs \nFormat: JunhyehokServer -cp [client port] -mmf [MMF name]");
                        Environment.Exit(0);
                        break;
                }
            }

            //=======================AGENT CONNECT================================
            Console.WriteLine("Connecting to Agent...");
            string agentInfo = "127.0.0.1:40000";
            Socket agentSocket = Connect(agentInfo);
            BackendHandle agent = new BackendHandle(agentSocket);
            agent.StartSequence();

            //======================AUTH SERVER CONNECT===========================
            Console.WriteLine("Connecting to Auth Server...");
            string authInfo = "";
            try { authInfo = System.IO.File.ReadAllText("backend.conf"); }
            catch (Exception e) { Console.WriteLine("\n" + e.Message); Environment.Exit(0); }
            Socket authSocket = Connect(authInfo);
            if (!authSocket.Connected)
            {
                Console.ReadLine();
                Environment.Exit(0);
            }
            BackendHandle auth = new BackendHandle(authSocket);
            AdvertiseToBackend(auth, clientPort);
            auth.StartSequence();

            //=======================REDIS CONNECT================================
            Console.WriteLine("Connecting to Redis...");
            RedisHandlerForFE redis = new RedisHandlerForFE(auth.So.LocalEndPoint.ToString());

            //======================INITIALIZE/UPDATE MMF=========================
            Console.WriteLine("Initializing lobby and rooms...");
            Console.WriteLine("Updating Memory Mapped File...");
            ReceiveHandle recvHandle = new ReceiveHandle(authSocket, redis, mmfName);

            //===================CLIENT SOCKET ACCEPT===========================
            Console.WriteLine("Accepting clients...");
            string address = "http://+:" + clientPort + "/wsJinhyehok/";

            HttpListener listener = new HttpListener();
            listener.Prefixes.Add(address);
            listener.Start();

            while (true)
            {
                HttpListenerContext listenerContext = listener.GetContext();
                if (listenerContext.Request.IsWebSocketRequest)
                {
                    ClientHandle client = new ClientHandle(listenerContext);
                    client.StartSequence();
                }
                else
                {
                    listenerContext.Response.StatusCode = 400;
                    listenerContext.Response.Close();
                }
            }
        }
        public static async void StartAcceptAsync(string listenerPrefix)
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add(listenerPrefix);
            listener.Start();

            while (true)
            {
                //HttpListenerContext listenerContext = listener.GetContext();
                HttpListenerContext listenerContext = await HelperExtensions.GetContextAsync(listener);
                if (listenerContext.Request.IsWebSocketRequest)
                {
                    ClientHandle client = new ClientHandle(listenerContext);
                    client.StartSequence();
                }
                else
                {
                    listenerContext.Response.StatusCode = 400;
                    listenerContext.Response.Close();
                }
            }
        }
        public static void AdvertiseToBackend(BackendHandle backend, string clientPort)
        {
            FBAdvertiseRequest fbAdvertiseRequest;
            char[] ip = ((IPEndPoint)backend.So.LocalEndPoint).Address.ToString().ToCharArray();
            char[] ipBuffer = new char[15];
            Array.Copy(ip, ipBuffer, ip.Length);
            fbAdvertiseRequest.ip = ipBuffer;
            fbAdvertiseRequest.port = int.Parse(clientPort);
            byte[] advertiseBytes = Serializer.StructureToByte(fbAdvertiseRequest);
            backend.So.SendBytes(new Packet(new Header(Code.ADVERTISE, (ushort)advertiseBytes.Length), advertiseBytes));
        }
        public static Socket Connect(string info)
        {
            string host;
            int port;
            string[] hostport = info.Split(':');
            host = hostport[0];
            if (!int.TryParse(hostport[1], out port))
            {
                Console.Error.WriteLine("port must be int. given: {0}", hostport[1]);
                Environment.Exit(0);
            }

            Socket so = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            IPAddress ipAddress = IPAddress.Parse(host);

            Console.WriteLine("Establishing connection to {0}:{1} ...", host, port);

            try
            {
                so.Connect(ipAddress, port);
                Console.WriteLine("Connection established.\n");
            }
            catch (Exception)
            {
                Console.WriteLine("Peer is not alive.");
            }

            return so;
        }
    }

    // This extension method wraps the BeginGetContext / EndGetContext methods on HttpListener as a Task, using a helper function from the Task Parallel Library (TPL).
    // This makes it easy to use HttpListener with the C# 5 asynchrony features.
    public static class HelperExtensions
    {
        public static Task<HttpListenerContext> GetContextAsync(this HttpListener listener)
        {
            return Task.Factory.FromAsync<HttpListenerContext>(listener.BeginGetContext, listener.EndGetContext, TaskCreationOptions.None);
        }
    }
}
