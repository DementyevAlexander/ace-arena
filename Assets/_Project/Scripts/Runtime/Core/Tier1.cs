namespace Arena.Core
{
    /// <summary>Таблицы тира 1 из DandyAce (tiersjson): урон карт, урон и здоровье врагов по «качеству» WEAK-…STRONG+.</summary>
    public static class Tier1
    {
        public const int WeakMinus = 0, Weak = 1, WeakPlus = 2, MidMinus = 3, Mid = 4, MidPlus = 5, StrongMinus = 6, Strong = 7, StrongPlus = 8;

        public static readonly int[] SkillDamage = { 75, 100, 125, 175, 200, 225, 275, 300, 325 };
        public static readonly int[] EnemyDamage = { 38, 42, 50, 59, 65, 79, 100, 116, 139 };
        public static readonly int[] EnemyHp = { 225, 350, 475, 600, 725, 850, 975, 1100, 1225 };
    }
}
