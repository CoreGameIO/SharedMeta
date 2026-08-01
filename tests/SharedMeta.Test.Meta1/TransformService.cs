using SharedMeta.Core;

namespace SharedMeta.Test.Meta1
{
    [MetaServiceImpl(typeof(ITransformService), typeof(TransformState))]
    public partial class TransformService : ITransformService
    {
        public string MoveExplicit(Coord position, int tag) => Record(position, tag);

        public string MoveAuto(Coord position, int tag) => Record(position, tag);

        public string MoveSkip(Coord position, int tag) => Record(position, tag);

        public string MoveMixed(int lead, Coord position, int tag) => $"{lead}|{Record(position, tag)}";

        public void AddToken(int id, string label)
        {
            State.Tokens.Add(new Token { Id = id, Label = label });
        }

        public string TouchToken(Token token, int tag)
        {
            State.LastLabel = token.Label;
            State.LastTag = tag;
            State.Calls++;
            return $"{token.Id}:{token.Label}:{tag}";
        }

        public string PeekCoord(Coord position, int tag) => $"{position.X}:{position.Y}:{position.Origin}:{tag}";

        public string PeekPlain(int first, int second) => $"{first}:{second}";

        private string Record(Coord position, int tag)
        {
            var state = State;
            state.LastX = position.X;
            state.LastY = position.Y;
            state.LastOrigin = position.Origin;
            state.LastTag = tag;
            state.Calls++;
            return $"{position.X}:{position.Y}:{position.Origin}:{tag}";
        }
    }
}
