﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExempleMVVM.Modeles;
using System.Net.NetworkInformation;

namespace ExempleMVVM.Modules
{

    /// <summary>
    /// Protocole utilisant UDP 50000 permettant de clavarder avec plusieurs utilisateurs sans avoir
    /// un serveur centralisé. De plus, on peut initialiser une conversation crypter en AES128
    /// utilisant le TCP (port aléatoire).
    /// </summary>
    public class ProtocoleSJ
    {
        #region Public Methods

        /// <summary>
        /// Représente le chat global.
        /// </summary>
        public static Conversation conversationGlobale;

        /// <summary>
        /// Profil utilisé par l'application.
        /// </summary>
        public static Profil profilApplication;

        /// <summary>
        /// Port utilisé pour le chat global.
        /// </summary>
        public const int port = 50000;

        /// <summary>
        /// Met ton adresse IP dans une liste.
        /// </summary>
        private static List<IPAddress> mesAdresse = new List<IPAddress>();

        /// <summary>
        /// Met un bool à True ou False dépendant du fait ou l'application écoute ou non.
        /// </summary>
        private static bool enEcoute;

        /// <summary>
        /// Fait une liste d'utilisateurs afin de pouvoir rafraichir la liste.
        /// </summary>
        private static List<Utilisateur> utilisateurTemp = new List<Utilisateur>();



        /// <summary>
        /// Un objet utilisé pour obtenir un chiffre de façon aléatoire.
        /// </summary>
        private static Random random = new Random();

        /// <summary>
        /// Permet de vérifier si le nom d'utilisateur d'un profil est déjà utilisé sur le réseau et
        /// de démarre l'écoute sur le port 50000 UDP pour répondre au demande des autres
        /// utilisateurs. Durant le processus de connexion, profil.ConnexionEnCours est égal à vrai.
        /// Si le nom d'utilisateur est utilisé, on ferme l'écoute sur le port 50000 UDP. Sinon,
        /// profil.Connecte est égal à vrai.
        /// </summary>
        /// <param name="profil">Profil utilisé dans l'application pour avoir l'état de l'application</param>
        public static async void Connexion(Profil profil)
        {
            conversationGlobale = profil.Conversations.Where(c => c.EstGlobale).First();

            //Si la converstion globale n'existe pas, elle est crée

            if (conversationGlobale == null)
            {
                conversationGlobale = new Conversation();
                conversationGlobale.EstGlobale = true;

                profil.Conversations.Add(conversationGlobale);
            }

            conversationGlobale.Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            conversationGlobale.Socket.EnableBroadcast = true;
            conversationGlobale.Socket.Bind(new IPEndPoint(IPAddress.Any, port));


            // Trouver nos propre adresse IP
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (UnicastIPAddressInformation information in ni.GetIPProperties().UnicastAddresses)
                {
                    if (information.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        mesAdresse.Add(information.Address);
                    }
                }
            }

            profilApplication = profil;
            profil.ConnexionEnCours = true;

            Recevoir(conversationGlobale);
            EnvoyerDiscovery();

            await Task.Delay(5000);

            foreach (Utilisateur utilisateur in utilisateurTemp)
            {
                if (!profilApplication.UtilisateursConnectes.Any(
                    u => u.Nom == utilisateur.Nom && u.IP == utilisateur.IP))
                {
                    profilApplication.UtilisateursConnectes.Add(utilisateur);
                }
            }

            utilisateurTemp.Clear();

            if (!profil.UtilisateursConnectes.Any(u => u.Nom == profil.UtilisateurLocal.Nom))
            {
                profil.Connecte = true;
            }
            else
            {
                enEcoute = false;
                conversationGlobale.Socket.Close();
            }

            profil.ConnexionEnCours = false;
        }

        /// <summary>
        /// Méthode permettant de fermer toutes les connexions en cours (UDP et TCP)
        /// </summary>
        public static void Deconnexion()
        {
            if (profilApplication != null && profilApplication.Connecte)
            {
                foreach (Conversation c in profilApplication.Conversations)
                {
                    if (c.EstPrivee)
                    {
                        TerminerConversationPrivee(c);
                    }
                    else
                    {
                        c.Socket.Close();
                    }
                }
            }
        }

        /// <summary>
        /// Permet d'ouvrir un port TCP et d'envoyer par UDP une demande de connexion en privée à l'utilisateur distant
        /// </summary>
        /// <param name="nouvelleConversation">Conversation privée contenant l'utilisateur distant</param>
        public static async void DemarrerConversationPrivee(Conversation nouvelleConversation)
        {
            byte[] cle = new byte[16];
            StringBuilder stringBuilderCle = new StringBuilder();
            short port = 0;
            string stringCle, stringPort;

            // Création de la clé
            random.NextBytes(cle);
            nouvelleConversation.Key = cle;

            // Ouverture du socket
            Socket socketEcoute = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socketEcoute.Bind(new IPEndPoint(IPAddress.Any, 0));
            socketEcoute.Listen(1);
            port = (short)((IPEndPoint)socketEcoute.LocalEndPoint).Port;

            // Mettre la clé et le port en string
            foreach (byte b in cle)
            {
                stringBuilderCle.Append(String.Format("{0:x2}", b));
            }
            stringCle = stringBuilderCle.ToString();
            stringPort = String.Format("{0:x4}", port);

            // Envois des informations à l'autre utilisateur
            EnvoyerDemendeConversationPrivee(stringCle, stringPort, nouvelleConversation.Utilisateur);

            // Attente de la connection de l'autre utilisateur
            await Task.Factory.StartNew(() =>
                {
                    nouvelleConversation.Socket = socketEcoute.Accept();
                    nouvelleConversation.Connecte = true;
                });
            socketEcoute.Close();

            // Commence à écouter l'autre utilisateur
            Recevoir(nouvelleConversation);
        }

        /// <summary>
        /// Méthode permettant d'envoyer un message sur la conversation en cours
        /// </summary>
        /// <param name="conversationEnCours">
        /// Conversation présentement sélectionnée pour envoyer le message
        /// </param>
        /// <param name="messageAEnvoyer">Message à envoyer à tous les utilisateurs</param>
        public static void EnvoyerMessage(Conversation conversationEnCours, string messageAEnvoyer)
        {
            Envoyer(conversationEnCours, "M" + messageAEnvoyer);
        }

        /// <summary>
        /// Méthode permettant d'envoyer un message en "broadcast" pour sonder tous les utilisateurs
        /// utilisant l'application sur le réseau. Par la suite, on peut rafraîchir la liste profilUtilisateursConnectes.
        /// </summary>
        public static async void RafraichirListeUtilisateursConnectes()
        {
            while (profilApplication.Connecte)
            {
                // Envoit un discovery
                EnvoyerDiscovery();
                await Task.Delay(5000);
                List<Utilisateur> listeASupprimer = new List<Utilisateur>();

                // Trouver les utilisateurs qui se sont ajouté ou sont partis
                foreach (Utilisateur vieuxUtilisateur in profilApplication.UtilisateursConnectes)
                {
                    Utilisateur utilisateur = utilisateurTemp.Find((u) =>
                    vieuxUtilisateur.Nom == u.Nom && vieuxUtilisateur.IP == u.IP);
                    if (utilisateur == null)
                    {
                        listeASupprimer.Add(vieuxUtilisateur);
                    }
                    else
                    {
                        utilisateurTemp.Remove(utilisateur);
                    }
                }

                // Supprimme les utilisateurs ayant quittés
                foreach (Utilisateur utilisateurASupprimer in listeASupprimer)
                {
                    profilApplication.UtilisateursConnectes.Remove(utilisateurASupprimer);
                }

                // Ajoute les nouveaux utilisateurs
                foreach (Utilisateur utilisateurAAjouter in utilisateurTemp)
                {
                    profilApplication.UtilisateursConnectes.Add(utilisateurAAjouter);
                }
                utilisateurTemp.Clear();
                listeASupprimer.Clear();
            }
        }

        /// <summary>
        /// Permet de fermer correctement une conversation privée
        /// </summary>
        /// <param name="conversation">Conversation à fermer</param>
        public static void TerminerConversationPrivee(Conversation conversation)
        {
            Envoyer(conversation, "Q");

            conversation.Socket.Shutdown(SocketShutdown.Both);
            conversation.Socket.Close();
            conversation.Connecte = false;
        }

        /// <summary>
        /// Ferme la conversation privée après qu'elle est étée quittée par l'autre
        /// utilisateur.
        /// </summary>
        /// <param name="conversation">Converstaion terminée</param>
        public static void RecevoirFinConversationPrivee(Conversation conversation)
        {
            conversation.Socket.Shutdown(SocketShutdown.Both);
            conversation.Socket.Close();
            conversation.Connecte = false;
        }


        #region reception

        /// <summary>
        /// Permet de recevoir un message en mode global
        /// </summary>
        /// <param name="conversation"> La conversation ou le paquet sera envoyé </param>
        /// <param name="message"> Le message qui est envoyé dans le paquet </param>
        /// <param name="endPoint"> L'adresse IP qui envoies le paquet </param>
        public static void RecevoirMessage(Conversation conversation, string message, EndPoint endPoint)
        {
            Utilisateur envoyeur;
            if (conversation.EstGlobale)
            {
                envoyeur = TrouverUtilisateurSelonEndPoint(endPoint);
            }
            else
            {
                envoyeur = conversation.Utilisateur;
            }

            IPAddress adresse = ((IPEndPoint)endPoint).Address;
            if (!EstMonAdresse(adresse))
            {
                LigneConversation ligne = new LigneConversation();
                ligne.Message = message;
                ligne.Utilisateur = envoyeur;
                conversation.Lignes.Add(ligne);

            };
        }

        /// <summary>
        /// Retourne l'utilisateur à qui apartient l'endpoint
        /// </summary>
        /// <param name="endPoint">EndPoint</param>
        /// <returns>L'utilisateur, null s'il n'est pas trouvé</returns>
        public static Utilisateur TrouverUtilisateurSelonEndPoint(EndPoint endPoint)
        {
            string ip = ((IPEndPoint)endPoint).Address.ToString();
            Utilisateur resultat = null;

            if (profilApplication.UtilisateursConnectes.Count > 0)
            {
                IEnumerable<Utilisateur> utilisateursTrouve = profilApplication.UtilisateursConnectes.Where(u => u.IP == ip);
                if (utilisateursTrouve.Count() > 0)
                {
                    resultat = utilisateursTrouve.First();
                }
            }

            return resultat;
        }

        /// <summary>
        /// Permet de recevoir l'adresse IP ainsi que le Nom de l'utilisateur.
        /// </summary>
        /// <param name="conversation"> Reçoit le paquet dans la conversation </param>
        public static async void Recevoir(Conversation conversation)
        {
            enEcoute = true;
            bool socketLisible = true;

            while (enEcoute && socketLisible)
            {
                byte[] data = new byte[1024];

                int byteRead = 0;
                EndPoint otherEndPoint = new IPEndPoint(IPAddress.Any, 0);
                LigneConversation messageErreur = null;

                await Task.Factory.StartNew(() =>
                {
                    // Essaies de lire le message
                    try
                    {
                        if (conversation.Socket.Poll(500, SelectMode.SelectRead))
                        {
                            if (conversation.Socket.Available > 0)
                            {

                                if (conversation.EstPrivee)
                                {
                                    byteRead = conversation.Socket.Receive(data);
                                }
                                else
                                {
                                    byteRead = conversation.Socket.ReceiveFrom(data, ref otherEndPoint);
                                }
                            }
                            else
                            {
                                socketLisible = false;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        messageErreur = new LigneConversation();
                        messageErreur.Message = e.Message;
                        messageErreur.Utilisateur = new Utilisateur() { IP = "Erreur" };
                    }
                });
                // Prend le message et l'encode ou le décode selon l'événement
                string message = "";
                if (conversation.EstGlobale)
                {
                    message = Encoding.Unicode.GetString(data, 0, byteRead);
                }
                else
                {
                    if (byteRead > 0)
                    {
                        message = Decrypter(data, byteRead, conversation.Key);
                    }
                }

                // Interprete si des données ont été reçues
                if (byteRead > 0)
                {
                    Interpreter(conversation, otherEndPoint, message);
                }

                if (messageErreur != null)
                {
                    conversation.Lignes.Add(messageErreur);
                    messageErreur = null;
                }
            }
            if (conversation.EstPrivee)
            {
                profilApplication.Conversations.Remove(conversation);
            }
        }

        /// <summary>
        /// Interprête un message reçu. Ne fait rien si le message n'est pas valide.
        /// </summary>
        /// <param name="conversation">Conversation d'ou provient le message</param>
        /// <param name="otherEndPoint">Endpoint de celui qui envois le message si il s'agit de la conversation locale</param>
        /// <param name="message">Message reçu</param>
        public static void Interpreter(Conversation conversation, EndPoint otherEndPoint, string message)
        {
            if (message.Substring(0, 3) == "TPR")
            {
                switch (message[3])
                {
                    case 'D':
                        EnvoyerIdentification(conversation, otherEndPoint);
                        break;
                    case 'I':
                        if (conversation.EstGlobale)
                        {
                            RecevoirIdentification((IPEndPoint)otherEndPoint, message);
                        }
                        break;
                    case 'M':
                        RecevoirMessage(conversation, message.Substring(4), otherEndPoint);
                        break;
                    case 'P':
                        RecevoirDemandeConversationPrivee(otherEndPoint, message.Substring(4));
                        break;
                    case 'Q':
                        TerminerConversationPrivee(conversation);
                        break;
                }
            }
        }

        /// <summary>
        /// Reçoit l'identification d'un utilisateur
        /// </summary>
        /// <param name="endpoint"> Reçoit l'adresse IP de l'envoyeur </param>
        /// <param name="message"> Reçoit le message de l'envoyeur </param>
        public static void RecevoirIdentification(IPEndPoint endpoint, string message)
        {
            string nom = message.Substring(4);
            IPAddress adresse = endpoint.Address;

            if (!EstMonAdresse(adresse))
            {
                Utilisateur nouvelUtilisateur = new Utilisateur()
                {
                    Nom = nom,
                    IP = adresse.ToString()
                };
                utilisateurTemp.Add(nouvelUtilisateur);
                // Ajouter le nouvel utilisateur aux utilisateurs connectés s'il n'y est pas déjà
                if (!profilApplication.UtilisateursConnectes.Any(u =>
                    u.IP == nouvelUtilisateur.IP && u.Nom == nouvelUtilisateur.Nom))
                {
                    profilApplication.UtilisateursConnectes.Add(nouvelUtilisateur);
                }

            }
        }

        /// <summary>
        /// Appelerpour débuter une conversation privée avec un utilisateur en ayant fait
        /// la demande.
        /// </summary>
        /// <param name="envoyeur">EndPoint de l'envoyeur</param>
        /// <param name="message">Message reçu, sans l'entête</param>
        public static async void RecevoirDemandeConversationPrivee(EndPoint envoyeur, string message)
        {
            Utilisateur autre = TrouverUtilisateurSelonEndPoint(envoyeur);

            Conversation nouvelleConversation = new Conversation();
            nouvelleConversation.Utilisateur = autre;
            nouvelleConversation.EstGlobale = false;

            string stringPort = message.Substring(0, 4);
            string stringCle = message.Substring(4); //Du cinquième quaractère à la fin

            int port = Int32.Parse(stringPort, System.Globalization.NumberStyles.HexNumber);
            byte[] cle = new byte[16];

            nouvelleConversation.Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            // Trouver la clé
            for (int i = 0; i < 16; i++)
            {
                cle[i] = byte.Parse(stringCle.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
            }
            nouvelleConversation.Key = cle;

            // Connection à l'autre utilisateur sur le port indiqué
            await Task.Factory.StartNew(() =>
            {
                nouvelleConversation.Socket.Connect(IPAddress.Parse(autre.IP), port);
                nouvelleConversation.Connecte = true;
            });

            profilApplication.Conversations.Add(nouvelleConversation);

            Recevoir(nouvelleConversation);
        }

        /// <summary>
        /// Retourne vrai si l'adresse passée en argument est la notre
        /// </summary>
        /// <param name="adresse">Adresse à vérifier</param>
        public static bool EstMonAdresse(IPAddress adresse)
        {
            return mesAdresse.Any(a => a.Equals(adresse));
        }

        #endregion reception

        #region Envois

        /// <summary>
        /// Envois un message en UDP ou TCP selon la conversation et l'encrypte
        /// si nécessaire 
        /// </summary>
        /// <param name="conversation"> Conversation à laquelle envoyer le message </param>
        /// <param name="message"> Message à envoyer </param>
        public static async void Envoyer(Conversation conversation, string message)
        {
            string messageComplet = "TPR" + message;

            if (conversation.EstGlobale)
            {
                byte[] data = Encoding.Unicode.GetBytes(messageComplet);
                await Task.Factory.StartNew(() =>
                {
                    // Envoyer juste aux bonnes personnes
                    foreach (Utilisateur utilisateur in profilApplication.UtilisateursConnectes)
                    {
                        IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(utilisateur.IP), port);
                        conversation.Socket.SendTo(data, endPoint);
                    }
                });
            }
            else // Conversation privée
            {
                byte[] data = Encrypter(messageComplet, conversation.Key);
                await Task.Factory.StartNew(() =>
                {
                    conversation.Socket.Send(data);
                });
            }
        }

        /// <summary>
        /// Envois un message en UDP au endPoint spécifié. À utiliser pour envoyer un
        /// message à quelqu'un qui n'est pas dans la liste d'utilisateur.
        /// </summary>
        /// <param name="endPoint">Destination</param>
        /// <param name="socket">Socket par lequel envoyer le message</param>
        /// <param name="message">Message à envoyer</param>
        public static async void Envoyer(EndPoint endPoint, Socket socket, string message)
        {
            await Task.Factory.StartNew(() =>
            {
                socket.SendTo(Encoding.Unicode.GetBytes("TPR" + message), endPoint);
            });
        }

        /// <summary>
        /// Envoie un message en broadcast
        /// </summary>
        /// <param name="conversation">Conversation ayant un socket UDP</param>
        /// <param name="message">Message à envoyer</param>
        public static void EnvoyerBroadcast(Conversation conversation, string message)
        {
            byte[] data = Encoding.Unicode.GetBytes("TPR" + message);
            foreach (var broadcast in ObtenirAdressesBroadcast())
            {
                conversation.Socket.SendTo(data, 0, data.Length, SocketFlags.None, new IPEndPoint(broadcast, 50000));
            }
        }

        /// <summary>
        /// Permet d'obtenir la liste des adresses Broadcast disponibles. La fonction élimine les adresses des cartes Loopback et des cartes qui ne sont pas branchées.
        /// </summary>
        /// <returns>La liste des adresses Broadcast disponibles. La fonction élimine les adresses des cartes Loopback et des cartes qui ne sont pas branchées.</returns>
        private static HashSet<IPAddress> ObtenirAdressesBroadcast()
        {
            HashSet<IPAddress> broadcasts = new HashSet<IPAddress>();
            foreach (var i in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    foreach (var ua in i.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            IPAddress broadcast = new IPAddress(BitConverter.ToUInt32(ua.Address.GetAddressBytes(), 0) | (BitConverter.ToUInt32(ua.IPv4Mask.GetAddressBytes(), 0) ^ BitConverter.ToUInt32(IPAddress.Broadcast.GetAddressBytes(), 0)));
                            broadcasts.Add(broadcast);
                        }
                    }
                }
            }
            return broadcasts;
        }


        /// <summary>
        /// Fonction servant à encoder une clé et les messages qui seront envoyés en TCP
        /// </summary>
        /// <param name="message"> Prend le message qui sera encodé à l'aide d'une clé aes </param>
        /// <param name="cle"> Clé Aes qui servira à encoder les messages </param>
        /// <returns> Retourne les octets du message enncodé </returns>
        public static byte[] Encrypter(string message, byte[] cle)
        {
            // Initiation de variables
            byte[] resultat;
            Aes aes = Aes.Create();

            if (cle.Length != 16)
            {
                throw new ArgumentException("La clé doit être de 128 bits");
            }

            aes.Key = cle;
            byte[] IV = new byte[16]; // IV de 0 pour que ça fonctionne
            for (int i = 0; i < 16; i++)
            {
                IV[i] = 2;
            }
            aes.IV = IV;

            // Encryption
            ICryptoTransform encrypteur = aes.CreateEncryptor(aes.Key, aes.IV);
            using (MemoryStream memoryStream = new MemoryStream())
            {
                using (CryptoStream cryptoStream = new CryptoStream(memoryStream, encrypteur, CryptoStreamMode.Write))
                {
                    byte[] data = Encoding.Unicode.GetBytes(message);
                    cryptoStream.Write(data, 0, data.Length);
                    cryptoStream.FlushFinalBlock();
                    resultat = memoryStream.ToArray();
                }
            }

            return resultat;
        }


        /// <summary>
        /// Fonction servant à décoder un message à l'aide d'une clé aes lors de conversation privée
        /// </summary>
        /// <param name="message"> Message à décoder </param>
        /// <param name="size"> Nombre d'octets à décrypter </param>
        /// <param name="cle"> Clé servant à décoder le message </param>
        /// <returns> Retourne les octets décodé sous une forme de message </returns>
        public static string Decrypter(byte[] message, int size, byte[] cle)
        {
            // Initiation de variables
            char[] buffer = new char[1024];
            Aes aes = Aes.Create();
            int charRead = 0;
            string resultat = "";

            if (cle.Length != 16)
            {
                throw new ArgumentException("La clé doit être de 128 bits");
            }

            aes.Key = cle;
            byte[] IV = new byte[16]; // IV de 0 pour que ça fonctionne
            for (int i = 0; i < 16; i++)
            {
                IV[i] = 2;
            }
            aes.IV = IV;

            // Encryption
            ICryptoTransform decrypteur = aes.CreateDecryptor(aes.Key, aes.IV);
            using (MemoryStream memoryStream = new MemoryStream())
            {
                memoryStream.Write(message, 0, size);
                memoryStream.Seek(0, SeekOrigin.Begin);
                using (CryptoStream cryptoStream = new CryptoStream(memoryStream, decrypteur, CryptoStreamMode.Read))
                {
                    using (StreamReader streamReader = new StreamReader(cryptoStream, Encoding.Unicode))
                    {
                        charRead = streamReader.Read(buffer,0,size);
                    }
                }
            }

            for (int i = 0; i < charRead; i++)
            {
                resultat += buffer[i];
            }
            return resultat;
        }

        /// <summary>
        /// Envois un message de discovery en broadcast
        /// </summary>
        public static async void EnvoyerDiscovery()
        {
            EnvoyerBroadcast(conversationGlobale, "D");
        }

        /// <summary>
        /// Envois le nom de l'utilisateur à la conversation fournie
        /// </summary>
        /// <param name="conversation"> La conversation ou sera envoyé l'identification </param>
        public static async void EnvoyerIdentification(Conversation conversation, EndPoint endpoint)
        {
            Envoyer(endpoint, conversation.Socket, "I" + profilApplication.Nom);
        }

        /// <summary>
        /// Envois une demende de conversation privée en indiquant à quel port
        /// et avec quelle clé communiquer
        /// </summary>
        /// <param name="stringCle">Chaine hexadécimale représentant la clé</param>
        /// <param name="stringPort">Chaine hexadécimale représentant la clé</param>
        /// <param name="utilisateur">Utilisateur à qui envoyer la demende</param>
        public static void EnvoyerDemendeConversationPrivee(string stringCle, string stringPort, Utilisateur utilisateur)
        {
            IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(utilisateur.IP), port);

            Envoyer(endPoint, conversationGlobale.Socket, "P" + stringPort + stringCle);
        }

        #endregion Envois

        #endregion Public Methods
    }
}
