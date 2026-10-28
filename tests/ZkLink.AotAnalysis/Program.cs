using System.Runtime.CompilerServices;

// The root belongs only to this analysis executable, never to the representative consumer.
return RuntimeFeature.IsDynamicCodeSupported ? 1 : 0;
