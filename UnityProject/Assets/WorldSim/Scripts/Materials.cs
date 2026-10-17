using UnityEngine;

namespace WorldSim.Presentation
{
    /// <summary>Runtime material creation that works with URP (preferred) or the built-in pipeline.</summary>
    public static class Materials
    {
        private static Shader _lit;

        public static Shader Lit
        {
            get
            {
                if (_lit == null) _lit = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                return _lit;
            }
        }

        public static Material Create(Color color)
        {
            var m = new Material(Lit);
            SetColor(m, color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.1f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.1f);
            return m;
        }

        public static Material Create(Texture2D texture)
        {
            Material m = Create(Color.white);
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texture);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", texture);
            return m;
        }

        public static void SetColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        public static void SetColor(MaterialPropertyBlock block, Color c)
        {
            block.SetColor("_BaseColor", c);
            block.SetColor("_Color", c);
        }

        public static Color FromRgb(int rgb) =>
            new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
    }
}
