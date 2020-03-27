using System;
using System.IO;
using System.Net.Http;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace app.Pages
{
    public class IndexModel : PageModel
    {
        public string ImageBytesBase64 { get; set; }
    
        public void OnGet()
        {
            var world = new Map(new[]
            {
                "########################################",
                "#                                      #",
                "#      #########                       #",
                "#         ###                          #",
                "###       ###            #             #",
                "###                      #             #",
                "##                     ###             #",
                "#          c           ###      ########",
                "#                      ###      ########",
                "#                        #             #",
                "###                                    #",
                "###                                    #",
                "########################################",
            });

            var camera = new Camera(world.CameraLocation, world) {DirectionInDegrees = 0};
            var renderer = new BitmapRenderer(600, 800);
            var result = camera.Snapshot(renderer.Width, true);
            var pixels = renderer.RenderBitmap(result.Columns, camera);
            
            var jpegByteArray = JpegSaver.SaveToJpeg(pixels);
            ImageBytesBase64 = Convert.ToBase64String(jpegByteArray);
        }
    }
    
    public class Camera
    {
        public Location2D Location2D { get; set; }
        public Map World { get; }
        public int Range { get; }
        public double FocalLength { get; }

        public double DirectionInDegrees
        {
            get => _directionInDegrees;
            set => _directionInDegrees = value % 360;
        }

        private double _directionInDegrees;

        public Camera(Location2D location, Map world, int range = 25, double focalLength = 0.8)
        {
            Location2D = location;
            World = world;
            Range = range;
            FocalLength = focalLength;
        }

        public RenderResult Snapshot(int renderWidth, bool includeDebugInfo = false)
        {
            var result = new RenderResult(renderWidth);

            Parallel.For(0, renderWidth, column =>
            {
                var x = (double) column / renderWidth - 0.5;
                var angle = Math.Atan2(x, FocalLength);

                var castDirection = ComputeDirection(DirectionInDegrees, angle);
                var ray = Ray(column, new Ray.SamplePoint(Location2D), castDirection);

                result.Columns[column] = ray[ray.Count - 1];

                if (includeDebugInfo)
                {
                    ray.ForEach(i => result.AllSamplePoints.Add(i));
                }
            });

            return result;
        }

        private static CastDirection ComputeDirection(double directionDegrees, double angle)
        {
            var radians = Math.PI / 180 * directionDegrees; 
            var directionInDegrees = radians + angle;
            return new CastDirection(directionInDegrees);
        }

        private Ray Ray(int column, Ray.SamplePoint origin, CastDirection castDirection)
        {
            var rayPath = new Ray(column);
            var currentStep = origin;

            while (true)
            {
                rayPath.Add(currentStep);

                var stepX = ComputeNextStepLocation(castDirection.Sin, castDirection.Cos, currentStep.Location.X, currentStep.Location.Y);
                var stepY = ComputeNextStepLocation(castDirection.Cos, castDirection.Sin, currentStep.Location.Y, currentStep.Location.X, true);

                var nextStep = stepX.Length < stepY.Length
                    ? Inspect(stepX, 1, 0, currentStep.Distance, castDirection)
                    : Inspect(stepY, 0, 1, currentStep.Distance, castDirection);

                if (nextStep.Surface.HasNoHeight)
                {
                    currentStep = nextStep;
                    continue;
                }

                if (nextStep.Distance > Range)
                {
                    return rayPath;
                }

                rayPath.Add(nextStep);
                return rayPath;
            }
        }

        private static Ray.SamplePoint ComputeNextStepLocation(double rise, double run, double x, double y, bool inverted = false)
        {
            var dx = run > 0 ? Math.Floor(x + 1) - x : Math.Ceiling(x - 1) - x;
            var dy = dx * (rise / run);

            var length = dx * dx + dy * dy;
            var location2D = new Location2D
            {
                X = inverted ? y + dy : x + dx,
                Y = inverted ? x + dx : y + dy
            };

            return new Ray.SamplePoint(location2D, length);
        }

        private Ray.SamplePoint Inspect(Ray.SamplePoint step, int shiftX, int shiftY, double distance, CastDirection castDirection)
        {
            var dx = castDirection.Cos < 0 ? shiftX : 0;
            var dy = castDirection.Sin < 0 ? shiftY : 0;
            
            step.Surface = DetectSurface(step.Location.X - dx, step.Location.Y - dy);
            step.Distance = distance + Math.Sqrt(step.Length);

            return step;
        }

        private Surface DetectSurface(double xDouble, double yDouble)
        {
            var x = (int) Math.Floor(xDouble);
            var y = (int) Math.Floor(yDouble);
            
            if (x < 0 || x > World.Size - 1 || y < 0 || y > World.Size - 1)
            {
                return Surface.Nothing;
            }

            return World.SurfaceAt(x, y);
        }

        private struct CastDirection
        {
            public double Sin { get; }
            public double Cos { get; }

            public CastDirection(double angle)
            {
                Sin = Math.Sin(angle);
                Cos = Math.Cos(angle);
            }
        }

        public struct RenderResult
        {
            public Ray.SamplePoint[] Columns { get; set; }
            public ConcurrentBag<Ray.SamplePoint> AllSamplePoints { get; }

            public RenderResult(int renderWidth)
            {
                Columns = new Ray.SamplePoint[renderWidth];
                AllSamplePoints = new ConcurrentBag<Ray.SamplePoint>();
            }
        }
    }
  
    public struct Location2D
    {
        public double X;
        public double Y;
    }
  
    public class Map
    {
        public List<string> Topology { get; }
        public int Size { get; }
        public Location2D CameraLocation { get; }

        public Map(IEnumerable<string> topology)
        {
            Topology = topology.ToList();
            Size = Topology.First().Length;

            var cameraY = Topology.IndexOf(Topology.Single(line => line.Contains("c")));
            var cameraX = Topology[cameraY].IndexOf("c", StringComparison.Ordinal);
            Topology[cameraY] = Topology[cameraY].Replace("c", " ");

            CameraLocation = new Location2D { X = cameraX, Y = cameraY };
        }

        public Surface SurfaceAt(int x, int y)
        { 
            // Detect various materials from our map, and their properties
            // But we only know about full height walls for now.

            var glyph = Topology[y][x];
            
            if (glyph == '#')
            {
                return new Surface {Height = 1};
            }

            return Surface.Nothing;
        }

        public string ToDebugString(IEnumerable<Ray.SamplePoint> markLocations)
        {
            var copy = new List<string>(Topology);
            foreach (var item in markLocations)
            {
                var intX = (int) item.Location.X;
                var intY = (int) item.Location.Y;

                var temp = copy[intY].ToCharArray();
                temp[intX] = '.';
                copy[intY] = new string(temp);
            }
            return string.Join(Environment.NewLine, copy);
        }
    }
  
    public class Ray : List<Ray.SamplePoint>
    {
        public int Column { get; }

        public Ray(int column)
        {
            Column = column;
        }

        public struct SamplePoint
        {
            public Location2D Location { get; set; }
            public double Length { get; set; }
            public double Distance { get; set; }
            public Surface Surface { get; set; }

            public SamplePoint(Location2D location2D, double length = 0, double distance = 0)
            {
                Location = location2D;
                Length = length;
                Distance = distance;
                Surface =  Surface.Nothing;
            }
        }
    } 
  
    public struct Surface
    {
        public double Height { get; set; }

        public bool HasNoHeight => Height <= 0;

        // Other surface properties here

        public static Surface Nothing { get; } = new Surface();
    } 
  
    public class BitmapRenderer
    {
        public int SampleHeight { get; }
        public int SampleWidth { get; }
        public int SampleScale { get; } = 1;

        public int Height => SampleHeight * SampleScale;
        public int Width => SampleWidth * SampleScale;

        public BitmapRenderer(int sampleHeight, int sampleWidth, int sampleScale = 1)
        {
            SampleScale = sampleScale;
            SampleHeight = sampleHeight / SampleScale;
            SampleWidth = sampleWidth / SampleScale;
        }

        public Rgba32?[,] RenderBitmap(IReadOnlyList<Ray.SamplePoint> columnData, Camera camera)
        {
            var pixels = new Rgba32?[SampleWidth, SampleHeight];

            Parallel.For(0, columnData.Count, column =>
            {
                var samplePoint = columnData[column];

                var height = (SampleHeight * samplePoint.Surface.Height) / (samplePoint.Distance / 2.5);
                height = height <= 0 ? 0 : height;
                height = Math.Ceiling(height);
                height = height > SampleHeight ? SampleHeight : height;

                var offset = (int) Math.Floor((SampleHeight - height) / 2);

                var texture = SelectTexture(samplePoint, camera);

                for (var y = 0; y < height; y++)
                {
                    var yCoordinate = SampleHeight - y - 1;
                    yCoordinate = yCoordinate < 0 ? 0 : yCoordinate;
                    yCoordinate -= offset;

                    pixels[column, yCoordinate] = texture;
                }
            });
            
            return pixels;
        }

        private static Rgba32 SelectTexture(Ray.SamplePoint samplePoint, Camera camera)
        {
            var percentage = (samplePoint.Distance / camera.Range) * 100;
            var brightness = 200 - ((200.00 / 100) * percentage);
            return new Rgba32((byte) brightness, (byte) brightness, (byte) brightness);
        }
    }
  
    public static class JpegSaver
    {
        public static byte[] SaveToJpeg(Rgba32?[,] pixels)
        {
            var width = pixels.GetLength(0);
            var height = pixels.GetLength(1);

            //https://cdn.glitch.com/bf87c409-56e4-4c5e-9e5c-b0694b2fdd99%2Fbg.jpg?v=1585306420493
          
            var client = new HttpClient();
            var response = client.GetAsync("https://cdn.glitch.com/bf87c409-56e4-4c5e-9e5c-b0694b2fdd99%2Fbg.jpg?v=1585306420493").GetAwaiter().GetResult();
            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
          
            using var img = Image.Load<Rgba32>(bytes);
            img.Mutate(x => x.Resize(width, height));

            Parallel.For(0, height, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var rgba32 = pixels[x, y];
                    if (rgba32 == null)
                    {
                        continue;
                    }

                    img[x, y] = rgba32.Value;
                }
            });

            var memoryStream = new MemoryStream();
            img.SaveAsJpeg(memoryStream);
            return memoryStream.ToArray();
        }
    }  
}
