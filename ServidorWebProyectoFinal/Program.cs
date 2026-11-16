using System.IO;              
using System.Text;            
using System.Net;             
using System.Net.Sockets;     
using System.IO.Compression;  

object lockLog = new object();

int puerto = 0;       
string archivos = ""; 
string archivoConfig = "config.txt";


if (File.Exists(archivoConfig))
{
    string[] lineasConfig = File.ReadAllLines(archivoConfig);

    foreach (string linea in lineasConfig)
    {
        if (string.IsNullOrWhiteSpace(linea) || linea.StartsWith("#"))
            continue;

        string[] partes = linea.Split('=', 2);
        if (partes.Length < 2) continue;

        string clave = partes[0].Trim().ToUpper();
        string valor = partes[1].Trim();

        if (clave == "PORT")
            puerto = int.Parse(valor);
        else if (clave == "ROOT")
            archivos = valor;
    }
}
else
{
    Console.WriteLine("AVISO: No se encontró config.txt. Usando valores por defecto.");
}

Directory.CreateDirectory(archivos);

Console.WriteLine($"[CONFIG] Puerto configurado: {puerto}");
Console.WriteLine($"[CONFIG] Carpeta raíz: {Path.GetFullPath(archivos)}");

TcpListener listener = new TcpListener(IPAddress.Any, puerto);
listener.Start();

Console.WriteLine("--------------------------------------------------");
Console.WriteLine($"[SERVIDOR] Escuchando peticiones en el puerto {puerto}...");
Console.WriteLine($"[INFO] Entrá desde tu navegador a: http://localhost:{puerto}");
Console.WriteLine("--------------------------------------------------");


while (true)
{
    Socket cliente = listener.AcceptSocket();
    _ = Task.Run(() => AtenderCliente(cliente, archivos, lockLog));
}

static void AtenderCliente(Socket cliente, string archivos, object lockLog)
{
    using (cliente)
    {
        try
        {
            
            string ipOrigen = ((IPEndPoint)cliente.RemoteEndPoint!).Address.ToString(); 

            
            byte[] buffer = new byte[8192];
            int bytesRecibidos = cliente.Receive(buffer);
            if (bytesRecibidos == 0) return;

            string request = Encoding.UTF8.GetString(buffer, 0, bytesRecibidos);
            string[] lineasReq = request.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            if (lineasReq.Length == 0 || string.IsNullOrWhiteSpace(lineasReq[0])) return;
            

            
            string primerLinea = lineasReq[0];
            string[] partesReq = primerLinea.Split(' ');
            if (partesReq.Length < 2) return;
            

            string metodo = partesReq[0].ToUpper();
            string ruta = partesReq[1];

            string queryString = "";
            string postBody = "";

            
            if (ruta.Contains("?"))
            {
                string[] partesUrl = ruta.Split('?', 2);
                ruta = partesUrl[0];
                queryString = partesUrl[1];
            }

            
            if (ruta == "/")
            {
                ruta = "/index.html";
            }

            
            if (metodo == "POST")
            {
                int indiceVacio = Array.IndexOf(lineasReq, "");
                if (indiceVacio != -1 && indiceVacio + 1 < lineasReq.Length)
                {
                    postBody = string.Join("\r\n", lineasReq, indiceVacio + 1, lineasReq.Length - (indiceVacio + 1));
                }
            
            }

            
            bool aceptaGzip = false;
            foreach (string linea in lineasReq)
            {
                if (linea.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase) &&
                    linea.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                {
                    aceptaGzip = true;
                    break;
                }
            }
                    
            
            string dirLogs = "logs";
            Directory.CreateDirectory(dirLogs);
            string archivoLog = Path.Combine(dirLogs, DateTime.Now.ToString("yyyy-MM-dd") + ".txt");

            
            string querySeguro = WebUtility.HtmlEncode(queryString);
            string postSeguro = WebUtility.HtmlEncode(postBody);

            string entradaLog = $"{DateTime.Now:HH:mm:ss} | IP: {ipOrigen} | {metodo} | Ruta: {ruta}";
            if (!string.IsNullOrEmpty(queryString)) entradaLog += $" | Query: {queryString}";
            if (!string.IsNullOrEmpty(postBody)) entradaLog += $" | POST Data: {postBody}";

            lock (lockLog)
            {
                File.AppendAllText(archivoLog, entradaLog + Environment.NewLine);
            }

            Console.WriteLine($"{entradaLog}");

            
            string rutaBase = Path.GetFullPath(archivos);
            if (!rutaBase.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                rutaBase += Path.DirectorySeparatorChar;
            }

            string rutaRelativa = ruta.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
            string rutaCompleta = Path.GetFullPath(Path.Combine(rutaBase, rutaRelativa));

            if (!rutaCompleta.StartsWith(rutaBase, StringComparison.OrdinalIgnoreCase))
            {
                string html403 = "<!DOCTYPE html><html><head><meta charset='UTF-8'><title>403 - Prohibido</title></head>" +
                                 "<body><h1>403 - Acceso Denegado</h1><p>No tenés permiso para acceder a esta ruta.</p></body></html>";
                EnviarRespuesta(cliente, "403 Forbidden", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html403));
                return;
            }

            
            FileInfo archivo = new FileInfo(rutaCompleta);

            if (archivo.Exists)
            {
            
                byte[] archivoBytes = File.ReadAllBytes(rutaCompleta);
                string tipoMime = ObtenerTipoMime(rutaCompleta);

            
                if (aceptaGzip)
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (GZipStream gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                        {
                            gzip.Write(archivoBytes, 0, archivoBytes.Length);
                        }
                        byte[] bytesComprimidos = ms.ToArray();
                        EnviarRespuesta(cliente, "200 OK", tipoMime, bytesComprimidos, "gzip");
                    }
                }
                else
                {      
                    EnviarRespuesta(cliente, "200 OK", tipoMime, archivoBytes);
                }
            }
            else
            {                
                string ruta404 = Path.Combine(rutaBase, "404error.html");
                byte[] cuerpo404;

                if (File.Exists(ruta404))
                {
                    cuerpo404 = File.ReadAllBytes(ruta404);
                }
                else
                {                    
                    string fallback = "<h1>404 - Archivo no encontrado</h1>";
                    cuerpo404 = Encoding.UTF8.GetBytes(fallback);
                }                
                EnviarRespuesta(cliente, "404 Not Found", "text/html; charset=utf-8", cuerpo404);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Fallo en la conexión con el cliente: {ex.Message}");
        }
    }
}

static void EnviarRespuesta(Socket cliente, string codigoEstado, string contentType, byte[] cuerpo, string? contentEncoding = null)
{
    StringBuilder headers = new StringBuilder();
    headers.Append($"HTTP/1.1 {codigoEstado}\r\n");
    headers.Append($"Content-Type: {contentType}\r\n");
    headers.Append($"Content-Length: {cuerpo.Length}\r\n");
    if (!string.IsNullOrEmpty(contentEncoding))
    {
        headers.Append($"Content-Encoding: {contentEncoding}\r\n");
    }
    headers.Append("Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline';\r\n");
    headers.Append("X-Content-Type-Options: nosniff\r\n");

    headers.Append("Connection: close\r\n");
    headers.Append("\r\n"); 

    
    cliente.Send(Encoding.UTF8.GetBytes(headers.ToString()));
    cliente.Send(cuerpo);
}


static string ObtenerTipoMime(string rutaArchivo)
{
    string ext = Path.GetExtension(rutaArchivo).ToLower();
    return ext switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".svg" => "image/svg+xml",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };
}