using SharedMeta.Core;

namespace SharedMeta.Test.Meta2.Generic
{
    [MetaServiceImpl(typeof(IGenericService), typeof(GenericState))]
    public partial class GenericService : IGenericService
    {
        public int Add(int value, int tag)
        {
            State.Sum += value;
            State.LastTag = tag;
            State.Calls++;
            return (int)State.Sum;
        }

        public int AddOptimistic(int value, int tag) => Add(value, tag);

        public string MoveExplicit(Point position, int tag) => Record(position, tag);

        public string MoveAuto(Point position, int tag) => Record(position, tag);

        public string MoveSkip(Point position, int tag) => Record(position, tag);

        public string MoveMixed(int lead, Point position, int tag) => $"{lead}|{Record(position, tag)}";

        public string AdminMove(Point position, int tag) => Record(position, tag);

        public void AddMarker(int id, string label)
        {
            State.Markers.Add(new Marker { Id = id, Label = label });
        }

        public string TouchMarker(Marker marker, int tag)
        {
            State.LastLabel = marker.Label;
            State.LastTag = tag;
            State.Calls++;
            return $"{marker.Id}:{marker.Label}:{tag}";
        }

        public string PeekPoint(Point position, int tag) => $"{position.X}:{position.Y}:{position.Origin}:{tag}";

        public string PeekPlain(int first, int second) => $"{first}:{second}";

        private string Record(Point position, int tag)
        {
            State.LastX = position.X;
            State.LastY = position.Y;
            State.LastOrigin = position.Origin;
            State.LastTag = tag;
            State.Calls++;
            return $"{position.X}:{position.Y}:{position.Origin}:{tag}";
        }
    }
}
